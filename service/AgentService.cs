using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace LlamaPaddock.Agent
{
    /// <summary>
    /// The service: runs the Node agent with service mode on and keeps its output in logs\agent.log.
    /// Restarting is the service manager's job: when Node exits on its own, the service exits with
    /// an error and the recovery actions the installer set start it again.
    /// </summary>
    sealed class AgentService : ServiceBase
    {
        public const string Name = "llama-paddock-agent";
        const long LogMax = 5 * 1024 * 1024;
        const int StopWaitMs = 20_000;

        readonly Options options;
        readonly object logLock = new object();
        JobObject job;
        Process node;
        volatile bool stopping;

        public AgentService(Options options)
        {
            this.options = options;
            ServiceName = Name;
            CanStop = true;
            CanShutdown = true;
            // the installer registers the event source; the service account can't
            AutoLog = false;
        }

        string LogPath => Path.Combine(options.DataDir, "logs", "agent.log");

        protected override void OnStart(string[] args)
        {
            Directory.CreateDirectory(Path.Combine(options.DataDir, "logs"));
            Log($"starting {options.Node} {options.Script}");
            var psi = new ProcessStartInfo(options.Node, Quote(options.Script))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = options.DataDir,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PADDOCK_AGENT_MODE"] = "service";
            psi.EnvironmentVariables["PADDOCK_AGENT_DATA"] = options.DataDir;

            job = new JobObject();
            node = new Process { StartInfo = psi, EnableRaisingEvents = true };
            node.OutputDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
            node.ErrorDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
            node.Exited += OnNodeExited;
            node.Start();
            // before Node has had time to start Ollama, so everything it starts is in the job
            job.Add(node);
            node.BeginOutputReadLine();
            node.BeginErrorReadLine();
        }

        void OnNodeExited(object sender, EventArgs e)
        {
            if (stopping) return;
            int code = node.ExitCode;
            Log($"the agent exited with code {code}");
            Event($"The agent exited with code {code}. See {LogPath}.", EventLogEntryType.Error);
            job.Dispose();
            // exiting without reporting "stopped" counts as a failure, so the recovery actions restart the service
            Environment.Exit(1);
        }

        protected override void OnStop() => StopAgent();

        protected override void OnShutdown() => StopAgent();

        /// <summary>Windows has no SIGTERM for a console-less child: closing its stdin asks Node to stop Ollama and exit.</summary>
        void StopAgent()
        {
            stopping = true;
            if (node == null) return;
            RequestAdditionalTime(StopWaitMs + 5_000);
            try
            {
                if (!node.HasExited)
                {
                    node.StandardInput.Close();
                    if (!node.WaitForExit(StopWaitMs)) Log("the agent didn't stop in time; stopping everything it started");
                }
                if (node.HasExited) Log($"the agent stopped (code {node.ExitCode})");
            }
            catch (Exception ex)
            {
                Log($"stopping the agent: {ex.Message}");
            }
            // kills whatever is left: Node, Ollama, model runners
            job?.Dispose();
        }

        void Log(string line)
        {
            lock (logLock)
            {
                try
                {
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > LogMax)
                    {
                        string old = Path.Combine(options.DataDir, "logs", "agent-1.log");
                        File.Delete(old);
                        File.Move(LogPath, old);
                    }
                    File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}", new UTF8Encoding(false));
                }
                catch
                {
                    // a full disk mustn't take the agent down
                }
            }
        }

        static void Event(string message, EventLogEntryType type)
        {
            try { EventLog.WriteEntry(Name, message, type); } catch { /* source not registered */ }
        }

        static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
    }
}
