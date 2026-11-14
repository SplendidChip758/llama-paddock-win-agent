# Instructions for AI coding agents

This repository holds the Windows host agent of [llama-paddock](https://github.com/SplendidChip758/llama-paddock). The Linux agent lives in [llama-paddock-agent](https://github.com/SplendidChip758/llama-paddock-agent). Read [CONTRIBUTING.md](CONTRIBUTING.md) for the process. Issues, the roadmap and the user documentation live in llama-paddock.

## Layout

| Path | What |
|---|---|
| `agent/` | The agent: a .NET 10 program (`paddock-agent.exe`) that runs as a LocalSystem Windows service. `Api.cs` has the routes; `Installer.cs` the `install`, `uninstall`, `detect` and `stop` commands the setup runs |
| `tray/` | The tray app (`paddock-agent-tray.exe`): starts at sign-in, and does in the Ollama user's session what the service can't (start Ollama, announce new variables), over the pipe in `agent/HelperPipe.cs`. Links the shared files from `agent/` |
| `setup/` | The Inno Setup wizard, `llama-paddock-agent-setup.exe`. It only asks; `paddock-agent.exe install` does the work |
| `.github/scripts/build-setup.ps1` | Publishes both programs self-contained and builds the setup and `SHA256SUMS` |
| `test/compare.mjs` | Runs the C# agent and the Node.js agent it replaced (`test/legacy/`) side by side and compares their answers |
| `test/service.ps1` | CI only: the setup end to end, over a running Node.js agent |
| `test/fake-ollama/` | A stand-in `ollama.exe` for the tests |

## Rules

- **Keep the HTTP API in step with the Linux agent** in llama-paddock-agent. Same routes, same request and response shapes. llama-paddock's `dashboard/lib/types.ts` (`AgentStats`, `EnvState`, …) describes them. Fields that only make sense on one platform are optional, and Linux-only routes answer 400 here (`POST /gpu/reload-uvm`). An API change needs a PR in both repos.
- **Answer as the Node.js agent did.** `test/compare.mjs` checks it, down to error messages; `Js.cs` holds the JavaScript behaviour that depends on (number parsing, dates, regex classes). Change both agents' behaviour only on purpose, and say so in the test.
- **Dashboards have to keep working with older agents, and agents with older dashboards.** Add fields and routes instead of changing existing ones. llama-paddock's dashboard handles a missing field.
- **`Api.Protocol`** goes up only for a change older dashboards can't follow (a route or field removed or changing meaning), and only with the maintainer's agreement. Never for fixes or new optional fields. Both agents share the protocol, so bump it here and in llama-paddock-agent together; the dashboard has to learn the new protocol first (`AGENT_PROTOCOL` in llama-paddock's `dashboard/lib/protocol.ts`), so release that before the agents. `/health` and `/stats` keep `version` and `protocol` in every protocol. Update the protocol badge in README.md with it.
- **Versions are this agent's own.** A release here says nothing about the Linux agent's version, and the other way round.
- **The service runs as SYSTEM; Ollama runs as its user.** Read the user's files and variables through `OllamaUser`, and do anything in their session (starting Ollama, announcing variables) through `IUserSession`, which goes to the tray app. Check `IUserSession.Ready` before changing anything, so a missing tray app never leaves Ollama stopped or settings half applied.
- **Keep the pipe's checks**: its ACL (the user and SYSTEM), the tray app serving SYSTEM only, the service talking only to a pipe served by the Ollama user's process, identification-level impersonation.
- Keep the setup's AppId, the service name, the data folder (`%ProgramData%\llama-paddock-agent`) and the names in `agent.json`: installed agents are updated in place, and the setup moves Node.js agents over.
- Routes live in the `Routes` table in `Api.cs` as `"METHOD /path"` handlers. Every route except `GET /health` needs `Authorization: Bearer <PADDOCK_AGENT_TOKEN>`. Keep that.
- Run external commands with an argument list, never through a shell (`Run.Command`, with a time limit). Start Ollama only through `Launcher`, which starts it detached.
- Validate everything from the request before it reaches a file, the registry or a command, e.g. variable names against `Js.KeyRe`.
- Configuration comes from `agent.json` and `PADDOCK_*` environment variables (`AgentConfig`). Add new ones there and in llama-paddock's `docs/agent-windows.md`.
- `PADDOCK_DRY_RUN=1` must never stop or start Ollama. Keep that working for new actions.
- When the API changes, change the simulated agent in llama-paddock's `test/sim/paddock-sim.mjs` too.
- No tokens, passwords or personal hostnames/IPs in code, logs or commits. Use the `192.168.30.x` example addresses that the docs use.

## Checks

On Windows with the .NET 10 SDK and Node, see CONTRIBUTING.md, Tests:

```bash
dotnet build agent -c Release -warnaserror
dotnet build tray -c Release -warnaserror
node test/compare.mjs agent/bin/Release/net10.0-windows/paddock-agent.exe <fake ollama.exe> --tray=tray/bin/Release/net10.0-windows/paddock-agent-tray.exe
```

CI also builds the setup and runs `test/service.ps1`, which installs a service: never run that on a PC you use.

## Commit messages and releases

semantic-release reads every commit message to pick the next version, so a wrong type ships a wrong version. **Read [.github/instructions/commit-message.instructions.md](.github/instructions/commit-message.instructions.md) before committing.** Never mark a change as breaking unless the maintainer agrees. Never edit `CHANGELOG.md` or the projects' `<Version>`: the release workflow writes and stamps them.

Branch from `dev` and open PRs against `dev`. Reference llama-paddock issues as `SplendidChip758/llama-paddock#N`. A closing keyword here doesn't close an issue there: say in the issue which PR here did the work.
