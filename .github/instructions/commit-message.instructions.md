---
description: "How to write commit messages: Conventional Commits, read by semantic-release to version and release the llama-paddock Windows agent"
---

# Commit messages

Releases are fully automated: semantic-release reads every commit message on `main` and `dev` to pick the next version and write `CHANGELOG.md`. A wrong type ships a wrong version.

## Format

[Conventional Commits](https://www.conventionalcommits.org):

```
<type>(<scope>): <summary>

<body: what changed and why, wrapped at ~72 columns>

<footers, e.g. Part of SplendidChip758/llama-paddock#12>
```

- Summary: imperative mood, lowercase, no trailing period, under ~72 characters. `report the GPU fan speed`, not `Reported GPU fan speed.`
- Body: explain why, plus anything a reviewer or user needs (new env vars, upgrade notes). Bullets are fine. Leave it out for trivial changes.
- Issues live in llama-paddock: reference them as `SplendidChip758/llama-paddock#N`. A closing keyword here doesn't close them.
- Every commit must be valid, not only the PR title. PRs into `dev` are squash-merged, but PRs into `main` (the `dev` promotion and hotfixes) get a merge commit, so their commits reach the changelog as they are. CI checks every commit in PRs into `main`.

## Types

| Type | For | Bump while 0.x |
|---|---|---|
| `feat` | a new user-facing feature | minor |
| `fix` | a bug fix | patch |
| `perf` | a performance improvement | patch |
| `revert` | reverting an earlier commit | patch |
| `docs` | README, docs, comments only | none |
| `refactor` | code change with no behaviour change | none |
| `style` | formatting, whitespace | none |
| `test` | tests | none |
| `build` | the release script, dependencies | none |
| `ci` | `.github/workflows` | none |
| `chore` | anything else that doesn't touch shipped code | none |

Pick the type by what users notice, not by which files changed.

## Scopes

Optional, but use one when it fits: `installer` (the setup and `paddock-agent.exe install`), `tray`, `ci`, `docs`.

## Breaking changes

**Don't mark a change as breaking (`!` or a `BREAKING CHANGE:` footer) unless the maintainer has agreed to it.**

Breaking is reserved for upgrades that need the user to do something by hand, such as an env var removed or changing meaning. An API change older dashboards can't follow raises `Api.Protocol` (see AGENTS.md) and needs the maintainer too. If the installer or the updater handles the change, it isn't breaking: put upgrade notes in the body instead. 1.0.0 is cut deliberately, never as a side effect of a commit.

## Never

- Never edit `CHANGELOG.md` or the projects' `<Version>`. The release workflow writes and stamps them.
- Never put tokens, passwords or personal hostnames/IPs in a commit message.

## Examples

```
feat: report the GPU's fan speed
fix: restart Ollama when the tray app isn't running
fix(installer): keep agent.json when reinstalling
docs: describe PADDOCK_AGENT_ENV_ALLOW
ci: build the setup with Inno Setup 6.4
```
