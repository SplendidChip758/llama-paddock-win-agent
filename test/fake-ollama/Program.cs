using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// `ollama serve` for tests: answers GET /api/version on OLLAMA_HOST, writes the log lines the agent
// reads (GPU discovery, a model runner start, a [GIN] line per request, OLLAMA_KEEP_ALIVE so the test
// can tell which environment it got) and starts one model runner child, as Ollama does for a model:
// llama-server.exe when a copy of this stub has that name next to it, else `ollama runner`.
static class Program
{
    static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0] : "";
        if (cmd == "runner")
        {
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        if (cmd != "serve")
        {
            Console.Error.WriteLine("fake ollama: only `serve` is supported");
            return 2;
        }

        string host = Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "";
        host = host.Replace("http://", "");
        if (host == "") host = "127.0.0.1:11434";
        if (!host.Contains(":")) host += ":11434";
        string[] hp = host.Split(':');
        var ip = hp[0] == "0.0.0.0" || hp[0] == "" ? IPAddress.Any : IPAddress.Parse(hp[0]);
        var listener = new TcpListener(ip, int.Parse(hp[1]));
        listener.Start();

        Log($"msg=\"Listening on {host} (version 0.0.0-fake)\"");
        Log($"msg=\"server config\" OLLAMA_KEEP_ALIVE={Environment.GetEnvironmentVariable("OLLAMA_KEEP_ALIVE") ?? ""}");
        Log("msg=\"inference compute\" id=GPU-00000000 library=cuda compute=8.9 name=CUDA0 description=\"Fake GPU\" total=\"24.0 GiB\"");
        // newer Ollama runs models as llama-server.exe: a copy of this stub under that name stands in for it
        string self = Process.GetCurrentProcess().MainModule.FileName;
        string llamaServer = Path.Combine(Path.GetDirectoryName(self), "llama-server.exe");
        Process.Start(new ProcessStartInfo(File.Exists(llamaServer) ? llamaServer : self, "runner") { UseShellExecute = false, CreateNoWindow = true });
        Log("msg=\"llama runner started in 1.25 seconds\"");

        while (true)
        {
            using (var client = listener.AcceptTcpClient())
            {
                try { Serve(client); } catch (IOException) { /* client went away */ }
            }
        }
    }

    static void Serve(TcpClient client)
    {
        var watch = Stopwatch.StartNew();
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        string request = reader.ReadLine() ?? "";
        while (!string.IsNullOrEmpty(reader.ReadLine())) { }
        string[] parts = request.Split(' ');
        string method = parts.Length > 0 ? parts[0] : "", path = parts.Length > 1 ? parts[1] : "";
        bool ok = method == "GET" && path == "/api/version";
        string body = ok ? "{\"version\":\"0.0.0-fake\"}" : "{\"error\":\"not found\"}";
        string head = $"HTTP/1.1 {(ok ? "200 OK" : "404 Not Found")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(head + body);
        stream.Write(bytes, 0, bytes.Length);
        string remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
        Console.Error.WriteLine($"[GIN] {DateTime.Now:yyyy/MM/dd - HH:mm:ss} | {(ok ? 200 : 404)} | {watch.Elapsed.TotalMilliseconds:0.000}ms | {remote} | {method,-8} \"{path}\"");
    }

    static void Log(string msg) => Console.Error.WriteLine($"time={DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} level=INFO source=fake.go:1 {msg}");
}
