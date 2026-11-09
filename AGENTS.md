# Instructions for AI coding agents

This repository holds the Windows host agent of [llama-paddock](https://github.com/SplendidChip758/llama-paddock). The Linux agent lives in [llama-paddock-agent](https://github.com/SplendidChip758/llama-paddock-agent). Read [CONTRIBUTING.md](CONTRIBUTING.md) for the process. Issues, the roadmap and the user documentation live in llama-paddock.

## Layout

| Path | What |
|---|---|
| `paddock-agent.mjs` | The agent: runs in the user's session as a Scheduled Task at logon, next to the Ollama desktop app |
| `install.ps1`, `uninstall.ps1` | Install and remove it (copy to `%LOCALAPPDATA%\llama-paddock-agent`, `agent.json`, the task, the firewall rule) |
| `.github/scripts/prepare-release.sh` | Stamps the version and builds the release assets |

The agent is one file: plain Node ESM with **zero npm dependencies**, running on Node 18.15+.

## Rules

- **Keep the HTTP API in step with the Linux agent** in llama-paddock-agent. Same routes, same request and response shapes. llama-paddock's `dashboard/lib/types.ts` (`AgentStats`, `EnvState`, …) describes them. Fields that only make sense on one platform are optional, and Linux-only routes answer 400 here (`POST /gpu/reload-uvm`). An API change needs a PR in both repos.
- **Dashboards have to keep working with older agents, and agents with older dashboards.** Add fields and routes instead of changing existing ones. llama-paddock's dashboard handles a missing field.
- **`PROTOCOL`** goes up only for a change older dashboards can't follow (a route or field removed or changing meaning), and only with the maintainer's agreement. Never for fixes or new optional fields. Both agents share the protocol, so bump it here and in llama-paddock-agent together; the dashboard has to learn the new protocol first (`AGENT_PROTOCOL` in llama-paddock's `dashboard/lib/protocol.ts`), so release that before the agents. `/health` and `/stats` keep `version` and `protocol` in every protocol. Update the protocol badge in README.md with it.
- **Versions are this agent's own.** A release here says nothing about the Linux agent's version, and the other way round.
- Keep the install paths, the task name and the certificate name (`llama-paddock-agent`): installed agents are updated in place.
- Routes live in the `routes` table as `"METHOD /path"` handlers. Every route except `GET /health` needs `Authorization: Bearer <PADDOCK_AGENT_TOKEN>`. Keep that.
- Run external commands with an argument array, never through a shell. Use the `run()` helper (`execFile` with a timeout) for commands whose output you wait on, and `spawn(…, { detached: true })` only for starting long-running processes such as Ollama.
- Validate everything from the request before it reaches a file or a command, e.g. env var names against `KEY_RE`.
- Configuration comes from `agent.json` and `PADDOCK_*` env vars, documented in the header comment. Add new ones there and in llama-paddock's `docs/agent-windows.md`.
- `PADDOCK_DRY_RUN=1` must never kill or start Ollama. Keep that working for new actions.
- When the API changes, change the simulated agent in llama-paddock's `test/sim/paddock-sim.mjs` too.
- No tokens, passwords or personal hostnames/IPs in code, logs or commits. Use the `192.168.30.x` example addresses that the docs use.

## Checks

```bash
node --check paddock-agent.mjs
bash -n .github/scripts/prepare-release.sh
```

CI also parses `install.ps1` and `uninstall.ps1` and starts the agent on a Windows runner.

## Commit messages and releases

semantic-release reads every commit message to pick the next version, so a wrong type ships a wrong version. **Read [.github/instructions/commit-message.instructions.md](.github/instructions/commit-message.instructions.md) before committing.** Never mark a change as breaking unless the maintainer agrees. Never edit `CHANGELOG.md` or the agent's `VERSION`: the release workflow writes them.

Branch from `dev` and open PRs against `dev`. Reference llama-paddock issues as `SplendidChip758/llama-paddock#N`. A closing keyword here doesn't close an issue there: say in the issue which PR here did the work.
