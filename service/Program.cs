using System;
using System.IO;
using System.ServiceProcess;

namespace LlamaPaddock.Agent
{
    /// <summary>Where the agent and its data are. Defaults are the installed layout; the flags are for development.</summary>
    sealed class Options
    {
        public string Node;
        public string Script;
        public string DataDir;

        public static Options Parse(string[] args, int start)
        {
            string here = AppDomain.CurrentDomain.BaseDirectory;
            var o = new Options
            {
                Node = Path.Combine(here, "node.exe"),
                Script = Path.Combine(here, "paddock-agent.mjs"),
                DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "llama-paddock-agent"),
            };
            for (int i = start; i < args.Length; i++)
            {
                string value = i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--node": o.Node = value; break;
                    case "--script": o.Script = value; break;
                    case "--data": o.DataDir = value; break;
                    default: throw new ArgumentException($"unknown option {args[i]}");
                }
                i++;
            }
            return o;
        }
    }

    static class Program
    {
        static readonly string[] Flags = { "no-firewall", "keep-data" };

        static int Main(string[] args)
        {
            // install / uninstall / detect: run by the setup, elevated
            if (args.Length > 0 && !args[0].StartsWith("--"))
            {
                var opts = new System.Collections.Generic.Dictionary<string, string>();
                for (int i = 1; i < args.Length; i++)
                {
                    string key = args[i].TrimStart('-');
                    if (Array.IndexOf(Flags, key) >= 0) opts[key] = "";
                    else if (i + 1 < args.Length) opts[key] = args[++i];
                    else { Console.Error.WriteLine($"{args[i]} needs a value"); return 2; }
                }
                return Installer.Run(args[0], opts);
            }
            try
            {
                // started by the service manager
                ServiceBase.Run(new AgentService(Options.Parse(args, 0)));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
    }
}
