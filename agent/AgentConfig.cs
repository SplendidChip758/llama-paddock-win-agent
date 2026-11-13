using System.Text.Json;

namespace LlamaPaddock.Agent;

/// <summary>
/// Settings from agent.json in the data folder, overridden by environment variables of the same name.
/// The names are the Node agent's, so an existing agent.json works unchanged.
/// </summary>
public sealed class AgentConfig
{
    readonly Dictionary<string, string> file;

    public string DataDir { get; }

    AgentConfig(string dataDir, Dictionary<string, string> file)
    {
        DataDir = dataDir;
        this.file = file;
    }

    public static AgentConfig Load(string dataDir)
    {
        var path = Path.Combine(dataDir, "agent.json");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            // older installers wrote a UTF-8 BOM
            var text = File.ReadAllText(path).TrimStart('﻿');
            try
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var p in doc.RootElement.EnumerateObject())
                    values[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
            }
            catch (JsonException e)
            {
                throw new InvalidOperationException($"{path} is not valid JSON: {e.Message}");
            }
        }
        return new AgentConfig(dataDir, values);
    }

    public string Get(string key, string fallback = "") =>
        Environment.GetEnvironmentVariable(key) ?? (file.TryGetValue(key, out var v) ? v : fallback);

    public string Token => Get("PADDOCK_AGENT_TOKEN");
    public string Bind => Get("PADDOCK_AGENT_BIND", "0.0.0.0");
    public int Port => int.TryParse(Get("PADDOCK_AGENT_PORT", "11435"), out var p) ? p : 11435;
    public bool HttpOn => !Get("PADDOCK_AGENT_HTTP", "on").Equals("off", StringComparison.OrdinalIgnoreCase);
    public bool DryRun => Get("PADDOCK_DRY_RUN") == "1";
    public string TlsKey => Get("PADDOCK_AGENT_TLS_KEY", Path.Combine(DataDir, "agent.key"));
    public string TlsCert => Get("PADDOCK_AGENT_TLS_CERT", Path.Combine(DataDir, "agent.crt"));
}
