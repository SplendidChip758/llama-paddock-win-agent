#!/usr/bin/env bash
# Called by semantic-release (@semantic-release/exec prepareCmd) with the next
# version: stamps it into the agent, then puts the release assets into dist/
# for @semantic-release/github to upload. Asset names carry no version, so the
# releases/latest/download/ links in the docs stay stable, and they match the
# names the agents had in llama-paddock-agent, so only the repository in a
# download link changes.
set -euo pipefail
version="$1"
cd "$(dirname "$0")/../.."

sed -i -E "s/^(const VERSION = \")[^\"]*(\";)/\1${version}\2/" paddock-agent.mjs

rm -rf dist
mkdir -p dist/llama-paddock-agent-windows
cp paddock-agent.mjs dist/paddock-agent-windows.mjs
# The installer copies the agent from its own folder, so it ships as a zip of
# the whole folder.
cp paddock-agent.mjs install.ps1 uninstall.ps1 dist/llama-paddock-agent-windows/
(cd dist && zip -qr llama-paddock-agent-windows.zip llama-paddock-agent-windows)
(cd dist && sha256sum *.mjs *.zip > SHA256SUMS)
