<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="logo/banner-dark.svg">
    <img alt="llama-paddock-win-agent" src="logo/banner-light.svg" width="638">
  </picture>
</p>

<p align="center">
  <a href="https://github.com/SplendidChip758/llama-paddock-win-agent/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/SplendidChip758/llama-paddock-win-agent?style=flat-square&color=0b7f62&label=release"></a>
  <a href="https://github.com/SplendidChip758/llama-paddock-win-agent/releases"><img alt="Latest prerelease" src="https://img.shields.io/github/v/release/SplendidChip758/llama-paddock-win-agent?include_prereleases&sort=semver&filter=*-dev*&style=flat-square&color=2563eb&label=prerelease"></a>
  <a href="LICENSE"><img alt="License: AGPL-3.0" src="https://img.shields.io/badge/license-AGPL--3.0-0b7f62?style=flat-square"></a>
  <a href="https://github.com/SplendidChip758/llama-paddock-win-agent/commits"><img alt="Last commit" src="https://img.shields.io/github/last-commit/SplendidChip758/llama-paddock-win-agent?style=flat-square"></a>
  <a href="https://github.com/SplendidChip758/llama-paddock-win-agent/stargazers"><img alt="Stars" src="https://img.shields.io/github/stars/SplendidChip758/llama-paddock-win-agent?style=flat-square"></a>
  <br>
  <a href="https://ollama.com"><img alt="Ollama" src="https://img.shields.io/badge/Ollama-compatible-151d29?style=flat-square&logo=ollama&logoColor=white"></a>
  <a href="#llama-paddock-windows-agent"><img alt="Node.js" src="https://img.shields.io/badge/Node.js-%E2%89%A518.15-151d29?style=flat-square&logo=nodedotjs&logoColor=white"></a>
  <a href="#llama-paddock-windows-agent"><img alt="No dependencies" src="https://img.shields.io/badge/dependencies-none-151d29?style=flat-square"></a>
  <a href="#install"><img alt="Platform: Windows" src="https://img.shields.io/badge/host-Windows-151d29?style=flat-square"></a>
  <a href="#versions-and-the-protocol"><img alt="Agent protocol 1" src="https://img.shields.io/badge/protocol-1-151d29?style=flat-square"></a>
</p>

<p align="center">
  <a href="https://llama-paddock.com">Website</a> ·
  <a href="#install">Install</a> ·
  <a href="https://github.com/SplendidChip758/llama-paddock/blob/main/docs/agent-windows.md">Documentation</a> ·
  <a href="https://github.com/SplendidChip758/llama-paddock">Dashboard</a> ·
  <a href="https://github.com/SplendidChip758/llama-paddock-agent">Linux agent</a> ·
  <a href="https://github.com/SplendidChip758/llama-paddock/issues">Issues</a>
</p>

## llama-paddock Windows agent

The Windows host agent for [llama-paddock](https://github.com/SplendidChip758/llama-paddock), a self-hosted dashboard for a fleet of Ollama hosts. It runs on a Windows PC next to the Ollama desktop app and gives the dashboard what Ollama's own API doesn't: system and GPU stats, Ollama's environment variables, restarting Ollama, its log, and the GPU check.

It runs as a Scheduled Task at logon, in your own session, so it can restart the Ollama tray app. It is one file of plain Node.js (18.15+) with no dependencies, and serves the same HTTP API as the [Linux agent](https://github.com/SplendidChip758/llama-paddock-agent) on port 11435, over HTTPS with its own key and, unless turned off, plain HTTP.

## Install

Download `llama-paddock-agent-windows.zip` from the [latest release](https://github.com/SplendidChip758/llama-paddock-win-agent/releases/latest), unzip it, and run the installer from the account that runs Ollama:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

It needs Node.js 18.15 or newer (`winget install OpenJS.NodeJS.LTS`). The [Windows agent docs](https://github.com/SplendidChip758/llama-paddock/blob/main/docs/agent-windows.md) cover the options, updating, and adding the PC to the dashboard. `uninstall.ps1` removes it.

Every release has `llama-paddock-agent-windows.zip` and `SHA256SUMS`, which lists the zip and the files in it. Releases up to 0.10.0-dev.1 also have `paddock-agent-windows.mjs`.

## Versions and the protocol

This agent is released on its own, apart from the dashboard and the Linux agent, so a Windows-only fix ships without a new Linux agent. Its version says nothing about which dashboards it works with. That's the **protocol**: a whole number in `GET /health` and `GET /stats`, shared by both agents. It only goes up when the API changes in a way older dashboards can't follow, and each dashboard supports a range of protocols. See [Agent protocol](https://github.com/SplendidChip758/llama-paddock/blob/main/docs/api.md#agent-protocol).

Releases from `main` are stable (`vX.Y.Z`). Releases from `dev` are prereleases (`vX.Y.Z-dev.N`). Versions up to 0.10.0-dev.1 were shared with the Linux agent, in llama-paddock and then llama-paddock-agent.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md). Issues and the roadmap live in [llama-paddock](https://github.com/SplendidChip758/llama-paddock/issues): open issues there, and reference them from here as `SplendidChip758/llama-paddock#N`.

Report security problems privately, as [SECURITY.md](SECURITY.md) describes.

## License

[AGPL-3.0](LICENSE), like llama-paddock.
