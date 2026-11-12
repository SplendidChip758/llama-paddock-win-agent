# Instructions for AI coding agents

This repository holds the Windows host agent of [llama-paddock](https://github.com/SplendidChip758/llama-paddock). The Linux agent lives in [llama-paddock-agent](https://github.com/SplendidChip758/llama-paddock-agent). Read [CONTRIBUTING.md](CONTRIBUTING.md) for the process. Issues, the roadmap and the user documentation live in llama-paddock.

## Layout

| Path | What |
|---|---|
| `paddock-agent.mjs` | The agent. In **service mode** (`PADDOCK_AGENT_MODE=service`) it runs `ollama serve` itself; in **task mode** (legacy) it runs in the user's session next to the Ollama desktop app |
| `service/` | `paddock-agent-service.exe` (C#, .NET Framework 4.8): the Windows service that runs the agent, and the `install`, `uninstall`, `detect` and `stop` steps the setup calls |
| `setup/llama-paddock-agent.iss` | The Inno Setup wizard that builds `llama-paddock-agent-setup.exe` |
| `build/node.json` | The Node.js version the setup bundles, with its SHA-256 |
| `.github/scripts/build-setup.ps1` | Builds the setup and `SHA256SUMS` (Windows) |
| `.github/scripts/prepare-release.sh` | Stamps the version into the agent |
| `test/service-mode.ps1`, `test/fake-ollama/` | Service mode against a stub `ollama.exe`, no admin needed |
| `test/service.ps1` | The setup end to end: migration, service, upgrade, uninstall. Admin; changes the machine |
| `install.ps1`, `uninstall.ps1` | The legacy scheduled-task install, for running next to the Ollama tray app. No longer released |

The agent is one file: plain Node ESM with **zero npm dependencies**, running on Node 18.15+. The service program uses only the .NET Framework.

## Installed layout

- `%ProgramFiles%\llama-paddock-agent`: `node.exe`, `paddock-agent.mjs`, `paddock-agent-service.exe`.
- `%ProgramData%\llama-paddock-agent`: `agent.json` (the token), `agent.key`, `agent.crt`, `agent-state.json`, `ollama-env.json` (Ollama's variables), `install-state.json` (what the installer changed outside its folders), `logs\agent.log` and `logs\server.log`. Only SYSTEM, Administrators and the service can open it.
- The service `llama-paddock-agent` runs as the virtual account `NT SERVICE\llama-paddock-agent`. The installer grants it read access to Ollama's folder and write access to the models folder, and records each grant so uninstall removes it.

Keep these names and paths, the setup's `AppId`, the firewall rule name (`llama-paddock agent`) and the certificate name (`llama-paddock-agent`): installed agents are updated in place. Legacy installs (`%LOCALAPPDATA%\llama-paddock-agent` and the task `llama-paddock-agent`) are found by those names.

## Rules

- **Keep the HTTP API in step with the Linux agent** in llama-paddock-agent. Same routes, same request and response shapes. llama-paddock's `dashboard/lib/types.ts` (`AgentStats`, `EnvState`, …) describes them. Fields that only make sense on one platform are optional, and Linux-only routes answer 400 here (`POST /gpu/reload-uvm`). An API change needs a PR in both repos.
- **Dashboards have to keep working with older agents, and agents with older dashboards.** Add fields and routes instead of changing existing ones. llama-paddock's dashboard handles a missing field.
- **`PROTOCOL`** goes up only for a change older dashboards can't follow (a route or field removed or changing meaning), and only with the maintainer's agreement. Never for fixes or new optional fields. Both agents share the protocol, so bump it here and in llama-paddock-agent together; the dashboard has to learn the new protocol first (`AGENT_PROTOCOL` in llama-paddock's `dashboard/lib/protocol.ts`), so release that before the agents. `/health` and `/stats` keep `version` and `protocol` in every protocol. Update the protocol badge in README.md with it.
- **Versions are this agent's own.** A release here says nothing about the Linux agent's version, and the other way round.
- **Never lose the token or the HTTPS key.** The dashboard pins the key: an install, update or migration that makes a new one cuts the host off until someone accepts it. `test/service.ps1` checks this; keep it passing.
- Routes live in the `routes` table as `"METHOD /path"` handlers. Every route except `GET /health` needs `Authorization: Bearer <PADDOCK_AGENT_TOKEN>`. Keep that.
- Run external commands with an argument array, never through a shell: `run()` (`execFile` with a timeout) in the agent, `Win.Run` in the service program. In the agent, `spawn` only for long-running processes such as Ollama.
- Validate everything from the request before it reaches a file or a command, e.g. env var names against `KEY_RE`.
- Configuration comes from `agent.json` and `PADDOCK_*` env vars, documented in the header comment. Add new ones there and in llama-paddock's `docs/agent-windows.md`.
- `PADDOCK_DRY_RUN=1` must never kill or start Ollama. Keep that working for new actions.
- The service program targets .NET Framework 4.8, part of Windows 10 and 11: no NuGet packages, nothing the PC would have to install.
- When the API changes, change the simulated agent in llama-paddock's `test/sim/paddock-sim.mjs` too.
- No tokens, passwords or personal hostnames/IPs in code, logs or commits. Use the `192.168.30.x` example addresses that the docs use.

## Checks

```bash
node --check paddock-agent.mjs
bash -n .github/scripts/prepare-release.sh
```

On Windows, with the .NET SDK:

```powershell
dotnet build service -c Release
dotnet build test/fake-ollama -c Release -o $env:TEMP\fake-ollama
.\test\service-mode.ps1 -FakeOllama $env:TEMP\fake-ollama\ollama.exe
```

CI also parses the PowerShell scripts, builds the setup and runs `test/service.ps1`, which installs it on the runner.

## Commit messages and releases

semantic-release reads every commit message to pick the next version, so a wrong type ships a wrong version. **Read [.github/instructions/commit-message.instructions.md](.github/instructions/commit-message.instructions.md) before committing.** Never mark a change as breaking unless the maintainer agrees. Never edit `CHANGELOG.md` or the agent's `VERSION`: the release workflow writes them.

Branch from `dev` and open PRs against `dev`. Reference llama-paddock issues as `SplendidChip758/llama-paddock#N`. A closing keyword here doesn't close an issue there: say in the issue which PR here did the work.
