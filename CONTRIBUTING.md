# Contributing

The Windows agent is part of [llama-paddock](https://github.com/SplendidChip758/llama-paddock), and its [Code of Conduct](https://github.com/SplendidChip758/llama-paddock/blob/main/CODE_OF_CONDUCT.md) applies here. Report security issues privately as described in [SECURITY.md](SECURITY.md).

Issues and the roadmap live in llama-paddock, on one tracker and one project. Open issues there, and reference them from commits and PRs here as `SplendidChip758/llama-paddock#N`.

## Local development

The agent is one file of plain Node.js (18.15+) with no dependencies. Run it without touching Ollama:

```powershell
$env:PADDOCK_AGENT_TOKEN = "test"; $env:PADDOCK_DRY_RUN = "1"
$env:PADDOCK_AGENT_TLS_KEY = "$env:TEMP\agent.key"; $env:PADDOCK_AGENT_TLS_CERT = "$env:TEMP\agent.crt"
node paddock-agent.mjs
curl.exe -H "Authorization: Bearer test" localhost:11435/stats
```

`PADDOCK_DRY_RUN=1` never kills or starts Ollama. The agent also starts on Linux or macOS, which is enough to check routes and shapes, but the stats and Ollama control only mean something on Windows.

Before pushing, run what CI runs:

```bash
node --check paddock-agent.mjs
bash -n .github/scripts/prepare-release.sh
```

### The service and the setup

The service program in `service/` is C# for .NET Framework 4.8; open `service/PaddockAgentService.csproj` in Visual Studio, or build it with the .NET SDK. Service mode can be tested without installing anything, against a stub `ollama.exe`:

```powershell
dotnet build service -c Release
dotnet build test/fake-ollama -c Release -o $env:TEMP\fake-ollama
.\test\service-mode.ps1 -FakeOllama $env:TEMP\fake-ollama\ollama.exe
```

`.github/scripts/build-setup.ps1` builds `dist\llama-paddock-agent-setup.exe`; it needs Inno Setup 6 (`winget install JRSoftware.InnoSetup`). `test\service.ps1` installs that setup over a faked scheduled-task install, upgrades and uninstalls it. It changes the machine, so CI runs it on its throwaway runner; run it yourself only on a test PC or VM.

CI parses the PowerShell scripts, starts the agent, runs both tests and builds the setup on a Windows runner.

### Testing with the dashboard

llama-paddock's scenario tests run its dashboard against simulated agents (`test/sim/paddock-sim.mjs`). When the API changes, change the simulated agent there too, and the Linux agent in [llama-paddock-agent](https://github.com/SplendidChip758/llama-paddock-agent), each in its own PR, and say in each PR which one to merge first.

## The rules

Read [AGENTS.md](AGENTS.md). In short: keep the HTTP API in step with the Linux agent, add fields and routes instead of changing existing ones, and bump `PROTOCOL` only for a change older dashboards can't follow, with the maintainer's agreement.

## Commit messages

Releases are fully automated: semantic-release reads every commit message to pick the next version. Read [.github/instructions/commit-message.instructions.md](.github/instructions/commit-message.instructions.md). In short:

- Conventional Commits: `<type>(<scope>): <summary>`.
- `feat` bumps the minor version and `fix`/`perf` the patch version. `docs`, `test`, `style`, `refactor`, `build`, `ci` and `chore` don't release.
- Never mark a change as breaking (`!`, `BREAKING CHANGE:`) unless the maintainer agrees.
- Never edit `CHANGELOG.md` or `VERSION` in the agent by hand.

## Branches and pull requests

Branch from `dev` and open the PR against `dev`; it is squash-merged, so its title becomes the changelog line. Merges to `dev` publish prereleases (`vX.Y.Z-dev.N`). `main` is stable and only receives `dev` when it ships, plus urgent hotfixes, with a merge commit. The release workflow merges `main` back into `dev` after a stable release.

## How a release happens

On a push to `main` or `dev` that changes the agent, the service program or the setup, the release workflow runs semantic-release (`.releaserc.json`). It stamps the version into the agent, updates `CHANGELOG.md`, tags the commit and publishes a GitHub release. A Windows job then builds `llama-paddock-agent-setup.exe` from the tag, with the Node.js pinned in `build/node.json`, and attaches it with `SHA256SUMS`, which lists the setup and the main files in it.

To move to a newer Node.js, change the version and both checksums in `build/node.json`: `node_exe_sha256` from the release's `SHASUMS256.txt` (`win-x64/node.exe`), and `license_sha256` of `LICENSE` at that tag.
