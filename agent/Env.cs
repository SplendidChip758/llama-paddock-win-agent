using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlamaPaddock.Agent;

/// <summary>
/// GET and PUT /env: the Ollama user's own environment variables, which Ollama for Windows reads.
/// Only variables that look like Ollama's (OllamaNames) or that were added through the dashboard
/// are ever shown, never Path, TEMP and the like, and only allowed ones can be changed.
/// </summary>
public sealed class Env(AgentConfig cfg, IUserSession session)
{
    // Variables the dashboard may set. Anything else (CUDA_INJECTION64_PATH, HSA_TOOLS_LIB, …) could
    // load code into Ollama, so a dashboard login or agent token must not be enough to set it.
    // The host can extend the list through PADDOCK_AGENT_ENV_ALLOW; variables already set that
    // aren't listed are kept as they are and reported read-only. Keep in step with the other agents.
    public string[] Allow { get; } =
    [
        "OLLAMA_*",
        "CUDA_VISIBLE_DEVICES", "CUDA_DEVICE_ORDER", "HIP_VISIBLE_DEVICES", "ROCR_VISIBLE_DEVICES", "GPU_DEVICE_ORDINAL",
        "HSA_OVERRIDE_GFX_VERSION", "GGML_CUDA_ENABLE_UNIFIED_MEMORY",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY",
        .. cfg.Get("PADDOCK_AGENT_ENV_ALLOW").Split(',').Select(Js.Trim).Where(s => s != ""),
    ];

    static readonly string[] SystemNames = ["path", "pathext", "temp", "tmp", "userprofile", "appdata", "localappdata", "systemroot", "windir", "comspec"];

    string StatePath => Path.Combine(cfg.DataDir, "agent-state.json");

    public bool Allowed(string name) => Allow.Any(p => p.EndsWith('*')
        ? name.ToUpperInvariant().StartsWith(p[..^1].ToUpperInvariant(), StringComparison.Ordinal)
        : name.ToUpperInvariant() == p.ToUpperInvariant());

    // agent-state.json: the variables added through the dashboard that don't look like Ollama's
    JsonObject LoadState()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(StatePath)) is JsonObject o) return o;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new JsonObject { ["customKeys"] = new JsonArray() };
    }

    static List<string> CustomKeys(JsonObject state) =>
        state["customKeys"] is JsonArray a ? a.Select(k => k?.GetValueKind() == JsonValueKind.String ? k.GetValue<string>() : null).OfType<string>().ToList() : [];

    void SaveState(JsonObject state) =>
        File.WriteAllText(StatePath, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    public sealed record State(
        string platform,
        Dictionary<string, string> managed,
        string override_path,
        string raw,
        Dictionary<string, string> effective,
        Dictionary<string, string> external,
        string[] allowed);

    /// <summary>
    /// The Ollama variables a freshly started Ollama gets: the PC's, then the user's on top, and the
    /// custom names that count as Ollama's.
    /// </summary>
    public (Dictionary<string, string> Vars, List<string> CustomKeys) OllamaVariables(OllamaUser user)
    {
        var customKeys = CustomKeys(LoadState());
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in new[] { user.MachineVariables(), user.UserVariables() ?? throw HttpError.NotSignedIn() })
            foreach (var (k, v) in scope) if (OllamaNames.IsManaged(k, customKeys)) vars[k] = v;
        return (vars, customKeys);
    }

    public State Read()
    {
        var user = session.RequireUser();
        var userVars = user.UserVariables() ?? throw HttpError.NotSignedIn();
        var machineVars = user.MachineVariables();
        var customKeys = CustomKeys(LoadState());
        // names as written; the user's and the PC's can differ in case, and the Node agent kept both
        var managed = userVars.Where(e => OllamaNames.IsManaged(e.Key, customKeys)).ToDictionary(StringComparer.Ordinal);
        var machineManaged = machineVars.Where(e => OllamaNames.IsManaged(e.Key, customKeys)).ToDictionary(StringComparer.Ordinal);
        var external = machineManaged.Where(e => !managed.ContainsKey(e.Key)).ToDictionary(StringComparer.Ordinal);
        var effective = new Dictionary<string, string>(machineManaged, StringComparer.Ordinal);
        foreach (var (k, v) in managed) effective[k] = v;
        return new State("windows", managed, @"HKCU\Environment", string.Join("\n", managed.Select(e => $"{e.Key}={e.Value}")),
            effective, external, Allow);
    }

    public async Task<object> Put(JsonElement body, Func<Task<object>> restart, CancellationToken ct)
    {
        var vars = Js.Property(body, "vars");
        if (vars is not { ValueKind: JsonValueKind.Object } obj) throw new HttpError(400, "body must be {vars: {...}, restart?: bool}");
        var entries = Js.Entries(obj);
        var before = Read();
        string? Existing(string k) => before.managed.FirstOrDefault(e => e.Key.ToLowerInvariant() == k.ToLowerInvariant()).Value;
        foreach (var (k, v) in entries)
        {
            if (!Js.KeyRe().IsMatch(k)) throw new HttpError(400, $"invalid variable name: {Js.Stringify(k)}");
            if (v.ValueKind != JsonValueKind.String) throw new HttpError(400, $"value of {k} must be a string");
            var value = v.GetString()!;
            if (value.AsSpan().IndexOfAny("\r\n\0") >= 0) throw new HttpError(400, $"newlines not allowed in value of {k}");
            if (SystemNames.Contains(k, StringComparer.OrdinalIgnoreCase)) throw new HttpError(400, $"{k} is a system variable and can't be managed here");
            if (!Allowed(k) && Existing(k) != value)
                throw new HttpError(400, $"{k} is not an Ollama variable; add it to PADDOCK_AGENT_ENV_ALLOW on the host to manage it here");
        }

        // announcing the change needs the session: without it, change nothing
        if (!await session.Ready(ct)) throw HttpError.NoHelper();

        var state = LoadState();
        // set the allowed vars given and remove the allowed ones that were dropped; anything outside
        // the allowlist stays exactly as it is, whether or not the request mentions it
        var changes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (k, v) in entries) if (Allowed(k)) changes[k] = v.GetString();
        foreach (var k in before.managed.Keys)
            if (!entries.Any(e => e.Key.Equals(k, StringComparison.OrdinalIgnoreCase)) && Allowed(k)) changes[k] = null;
        state["customKeys"] = new JsonArray(changes.Where(c => c.Value is not null && !OllamaNames.IsOllamaName(c.Key)).Select(c => c.Key)
            .Concat(before.managed.Keys.Where(k => !Allowed(k) && !OllamaNames.IsOllamaName(k)))
            .Distinct(StringComparer.Ordinal).Select(k => (JsonNode)JsonValue.Create(k)!).ToArray());

        // An unchanged value isn't written again, so one stored as %VAR% stays that way. An empty one
        // removes the variable, as SetEnvironmentVariable did for the Node agent.
        var writes = changes.Where(c => !(c.Value is not null && before.managed.TryGetValue(c.Key, out var old) && old == c.Value));
        session.RequireUser().WriteUserVariables(writes);
        SaveState(state);
        // Explorer reloads the variables, so Ollama started from it sees them
        await session.NotifyEnvironmentChanged(ct);

        var after = Read();
        var result = new Dictionary<string, object?> { ["written"] = after.override_path, ["content"] = after.raw };
        if (Js.Property(body, "restart") is not { ValueKind: JsonValueKind.False }) result["restart"] = await restart();
        return result;
    }
}
