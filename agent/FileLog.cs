using System.Text;

namespace LlamaPaddock.Agent;

/// <summary>
/// The agent's log in a known place: logs\agent.log in the data folder, next to agent.json, which
/// keeps it to SYSTEM and administrators. At 5 MB it moves to agent.log.1, replacing the one before.
/// </summary>
public sealed class FileLog(string dir) : ILoggerProvider
{
    const long MaxBytes = 5 * 1024 * 1024;
    readonly Lock gate = new();
    readonly string file = Path.Combine(dir, "agent.log");

    public ILogger CreateLogger(string category) => new Logger(this, category);
    public void Dispose() { }

    void Write(string line)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(dir);
                if (File.Exists(file) && new FileInfo(file).Length > MaxBytes) File.Move(file, file + ".1", true);
                File.AppendAllText(file, line, Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the log is a convenience */ }
        }
    }

    sealed class Logger(FileLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level != LogLevel.None;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level.ToString().ToLowerInvariant()} {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            log.Write(line + Environment.NewLine);
        }
    }
}
