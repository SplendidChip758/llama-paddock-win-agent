using LlamaPaddock.Tray;

// The agent's tray app, started at sign-in. It runs in the user's session and serves the service's
// requests there (HelperServer); the tray icon shows whether it can.
//   --headless          no icon, only the helper (tests, sessions without a desktop)
//   --allow-same-user   serve this user's own programs too, not only the service (tests)

using var single = new Mutex(true, @"Local\llama-paddock-agent-tray", out bool first);
if (!first) return 0;

using var stop = new CancellationTokenSource();
var server = new HelperServer(allowSameUser: args.Contains("--allow-same-user"));
var serving = Task.Run(() => server.Run(stop.Token));

if (args.Contains("--headless"))
{
    try { await serving; }
    catch (OperationCanceledException) { }
    return 0;
}

var ui = new Thread(() =>
{
    ApplicationConfiguration.Initialize();
    Application.Run(new TrayContext(server));
});
ui.SetApartmentState(ApartmentState.STA);
ui.Start();
ui.Join();
stop.Cancel();
try { await serving; }
catch (OperationCanceledException) { }
return 0;
