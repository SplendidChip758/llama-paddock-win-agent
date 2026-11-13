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

CI also parses `install.ps1` and `uninstall.ps1` with PowerShell and starts the agent on a Windows runner.

The C# agent in `agent/` is replacing the Node agent, so the two have to answer the same. On Windows, with the .NET 10 SDK and Node:

```bash
dotnet build agent -c Release
node test/compare.mjs agent/bin/Release/net10.0-windows/paddock-agent.exe
```

It runs both agents on the same log files, in dry run, and compares their answers. When you port a route, add its cases there.

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

On a push to `main` or `dev` that changes the agent or its installer, the release workflow runs semantic-release (`.releaserc.json`). It stamps the version into the agent, updates `CHANGELOG.md`, tags the commit and publishes a GitHub release with `llama-paddock-agent-windows.zip` (the agent, `install.ps1` and `uninstall.ps1`) and `SHA256SUMS`, which lists the zip and the files in it.
