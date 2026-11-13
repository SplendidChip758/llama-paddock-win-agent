using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace LlamaPaddock.Agent;

/// <summary>
/// HTTPS and plain HTTP on one port, like the Node agent: a TLS connection starts with a handshake
/// record (0x16). Plain HTTP is still read when it's turned off, so the agent can answer 426 with its
/// fingerprint and the dashboard can offer the switch to https://.
/// </summary>
public static class SharedPort
{
    public static void Configure(ListenOptions listen, Tls.Identity? tls)
    {
        listen.Protocols = HttpProtocols.Http1;
        if (tls is null) return;

        // Kestrel builds connection middleware from the last added to the first, so this capture
        // runs before the detector below is built: it holds the HTTP stage that follows TLS.
        ConnectionDelegate? afterTls = null;
        listen.Use(next => async ctx =>
        {
            var input = ctx.Transport.Input;
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            System.IO.Pipelines.ReadResult read;
            try { read = await input.ReadAsync(wait.Token); }
            catch (OperationCanceledException) { ctx.Abort(); return; }
            if (read.Buffer.IsEmpty) { ctx.Abort(); return; }
            bool handshake = read.Buffer.FirstSpan[0] == 0x16;
            // look without consuming: the next stage reads the same bytes
            input.AdvanceTo(read.Buffer.Start);
            await (handshake ? next(ctx) : afterTls!(ctx));
        });
        listen.UseHttps(tls.Certificate);
        listen.Use(next => afterTls = next);
    }
}
