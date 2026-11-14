using System.ComponentModel;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using LlamaPaddock.Agent;

namespace LlamaPaddock.Tray;

/// <summary>
/// Serves the service's requests on this user's pipe (HelperPipe): start Ollama, announce new
/// variables. One connection at a time; each is checked to come from SYSTEM, the service's account.
/// </summary>
public sealed class HelperServer(bool allowSameUser)
{
    readonly string sid = WindowsIdentity.GetCurrent().User!.Value;

    /// <summary>What the menu shows: serving, or why not.</summary>
    public string Status { get; private set; } = "starting";
    public event Action? StatusChanged;

    void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }

    public async Task Run(CancellationToken ct)
    {
        int refused = 0;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = Create(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // someone else holds the name: another tray app of this user, or a squatter the
                // service won't talk to. Try again in case it goes away.
                if (++refused == 3) SetStatus("not serving: the pipe is taken");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }
            refused = 0;
            SetStatus("ready");
            using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(ct);
                    await Serve(pipe, ct);
                }
                catch (IOException) { /* the service went away mid-request */ }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
        }
    }

    /// <summary>
    /// The pipe, open to this user and SYSTEM only. FirstPipeInstance fails if anyone else already
    /// serves the name, rather than sharing it with them.
    /// </summary>
    NamedPipeServerStream Create()
    {
        var security = new PipeSecurity();
        security.SetOwner(new SecurityIdentifier(sid));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(HelperPipe.SystemSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(HelperPipe.Name(sid), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }

    async Task Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        // this user's other programs may open the pipe too (its ACL can't tell them apart); only the
        // service is served, unless a test asks for this user
        var caller = Native.PipeClientProcessId(pipe.SafePipeHandle) is { } pid ? Native.ProcessUserSid(pid) : null;
        if (caller != HelperPipe.SystemSid && !(allowSameUser && caller == sid)) return;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        var request = await HelperPipe.ReadLine<HelperPipe.Request>(pipe, limit.Token);
        if (request is null) return;
        await HelperPipe.WriteLine(pipe, Handle(request), ct);
    }

    static HelperPipe.Response Handle(HelperPipe.Request request)
    {
        try
        {
            switch (request.Op)
            {
                case HelperPipe.Ping:
                    return new(true);
                case HelperPipe.NotifyEnvironment:
                    Native.BroadcastEnvironmentChange();
                    return new(true);
                case HelperPipe.Launch when request.Launch is { } launch:
                    Launcher.Start(launch);
                    return new(true);
                default:
                    return new(false, $"unknown request {request.Op}");
            }
        }
        catch (Win32Exception e)
        {
            return new(false, e.Message);
        }
    }
}
