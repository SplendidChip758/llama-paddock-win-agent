<!--
PR title = the squash-merge commit = the changelog entry, so it must be a
Conventional Commit, e.g. "feat: report the GPU's fan speed".
See CONTRIBUTING.md. Open PRs against `dev`, not `main`.
-->

## What and why

<!-- What does this change, and what problem does it solve? Reference issues as SplendidChip758/llama-paddock#123. -->

## Area

- [ ] Agent
- [ ] Installer / uninstaller
- [ ] CI / release
- [ ] Docs only

## How it was tested

<!-- What you ran and on what, e.g. "Windows 11 with Ollama 0.x, dry run", "test/compare.mjs", "installed with the setup from CI". -->

## Checklist

- [ ] PR title is a valid Conventional Commit
- [ ] Targets `dev` (or `main` for an urgent hotfix)
- [ ] `dotnet build agent` and `dotnet build tray` pass with `-warnaserror`, and `test/compare.mjs` passes
- [ ] API kept in step with the Linux agent (llama-paddock-agent PR: ) and the simulated agent in llama-paddock (PR: ), if the API changed
- [ ] `Api.Protocol` unchanged, or raised with the maintainer's agreement
- [ ] No tokens, passwords or personal hostnames/IPs in code, logs or screenshots
