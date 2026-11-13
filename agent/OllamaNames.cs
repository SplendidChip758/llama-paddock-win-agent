namespace LlamaPaddock.Agent;

/// <summary>
/// Which variables count as Ollama's: shown by GET /env, and replaced with current values when
/// Ollama is started. Shared with the tray app.
/// </summary>
public static class OllamaNames
{
    // /^(OLLAMA_|CUDA_|HIP_|ROCR_|HSA_|GGML_|GPU_DEVICE_ORDINAL$)/i, upper-casing A-Z only as JavaScript's /i does here
    public static bool IsOllamaName(string key)
    {
        var up = string.Create(key.Length, key, (span, src) =>
        {
            for (int i = 0; i < src.Length; i++) span[i] = src[i] is >= 'a' and <= 'z' ? (char)(src[i] - 32) : src[i];
        });
        return up == "GPU_DEVICE_ORDINAL" || new[] { "OLLAMA_", "CUDA_", "HIP_", "ROCR_", "HSA_", "GGML_" }.Any(p => up.StartsWith(p, StringComparison.Ordinal));
    }

    public static bool IsManaged(string key, List<string> customKeys) =>
        IsOllamaName(key) || customKeys.Any(k => k.ToLowerInvariant() == key.ToLowerInvariant());
}
