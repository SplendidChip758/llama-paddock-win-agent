# Contributing

The Windows agent is part of [llama-paddock](https://github.com/SplendidChip758/llama-paddock), and its [Code of Conduct](https://github.com/SplendidChip758/llama-paddock/blob/main/CODE_OF_CONDUCT.md) applies here. Report security issues privately as described in [SECURITY.md](SECURITY.md).

Issues and the roadmap live in llama-paddock, on one tracker and one project. Open issues there, and reference them from commits and PRs here as `SplendidChip758/llama-paddock#N`.

## Local development

The agent (`agent/`) and its tray app (`tray/`) are .NET 10 programs; you need the .NET 10 SDK on Windows. Run the agent from your own account, without touching Ollama:

```powershell
$env:PADDOCK_AGENT_DATA = "$env:TEMP\paddock-dev"; $env:PADDOCK_AGENT_TOKEN = "test"; $env:PADDOCK_DRY_RUN = "1"
dotnet run --project agent
curl.exe -H "Authorization: Bearer test" localhost:11435/stats
```

Started that way, the agent is the Ollama user itself, as in Visual Studio. Installed, it runs as a LocalSystem service and reaches the Ollama user's session through the tray app, which serves a named pipe that only that user and SYSTEM can open. `PADDOCK_DRY_RUN=1` never stops or starts Ollama.

### Tests

The C# agent replaced the Node.js agent, which stays in `test/legacy/` as the reference: the two have to answer the same. With the .NET 10 SDK and Node:

```bash
dotnet build agent -c Release
dotnet build tray -c Release
dotnet build test/fake-ollama -c Release -o "$TEMP/fake-ollama"
node test/compare.mjs agent/bin/Release/net10.0-windows/paddock-agent.exe "$TEMP/fake-ollama/ollama.exe" \
  --tray=tray/bin/Release/net10.0-windows/paddock-agent-tray.exe
```

It runs both agents on the same log files, in dry run, with a fake Ollama (a server and one model runner) for `/stats` to find, and compares their answers. `/stats` changes between calls, so it's compared by fields and types, plus the values that hold still. `PUT /env` writes your real user variables: the test only writes names starting with `OLLAMA_PADDOCK_TEST`, `PADDOCK_TEST` or `CUDA_PADDOCK_TEST`, removes them afterwards, and skips its writes when you have other Ollama variables. `--tray` also runs the agent as the service does (`PADDOCK_AGENT_MODE=helper`, the tray app with `--allow-same-user`), with and without the tray app. When you change a route, add its cases there.

CI also:
- adds `--restart`, which restarts the fake Ollama through each agent out of dry run. That stops every `ollama.exe` you run, so it skips itself when another Ollama is running.
- builds the setup (`.github/scripts/build-setup.ps1`, which needs [Inno Setup 6](https://jrsoftware.org/isinfo.php)) and runs `test/service.ps1`: installed over a running Node.js agent, the service through the tray app, an upgrade and an uninstall. It needs an administrator, installs a service and stops every `ollama.exe`, so don't run it on your own PC. Each CI run keeps the setup it built as an artifact, to try on a spare PC.

### Testing with the dashboard

llama-paddock's scenario tests run its dashboard against simulated agents (`test/sim/paddock-sim.mjs`). When the API changes, change the simulated agent there too, and the Linux agent in [llama-paddock-agent](https://github.com/SplendidChip758/llama-paddock-agent), each in its own PR, and say in each PR which one to merge first.

## The rules

Read [AGENTS.md](AGENTS.md). In short: keep the HTTP API in step with the Linux agent, add fields and routes instead of changing existing ones, and bump `PROTOCOL` only for a change older dashboards can't follow, with the maintainer's agreement.

## Commit messages

Releases are fully automated: semantic-release reads every commit message to pick the next version. Read [.github/instructions/commit-message.instructions.md](.github/instructions/commit-message.instructions.md). In short:

- Conventional Commits: `<type>(<scope>): <summary>`.
- `feat` bumps the minor version and `fix`/`perf` the patch version. `docs`, `test`, `style`, `refactor`, `build`, `ci` and `chore` don't release.
- Never mark a change as breaking (`!`, `BREAKING CHANGE:`) unless the maintainer agrees.
- Never edit `CHANGELOG.md` or the version in the projects by hand: the release build stamps it.

## Branches and pull requests

Branch from `dev` and open the PR against `dev`; it is squash-merged, so its title becomes the changelog line. Merges to `dev` publish prereleases (`vX.Y.Z-dev.N`). `main` is stable and only receives `dev` when it ships, plus urgent hotfixes, with a merge commit. The release workflow merges `main` back into `dev` after a stable release.

## How a release happens

On a push to `main` or `dev` that changes the agent, the tray app or the setup, the release workflow runs semantic-release (`.releaserc.json`). It updates `CHANGELOG.md`, tags the commit and publishes a GitHub release. A Windows job then builds the setup from the tag, with the version stamped into both programs, and attaches `llama-paddock-agent-setup.exe` and `SHA256SUMS`, which lists the setup and the programs in it.
