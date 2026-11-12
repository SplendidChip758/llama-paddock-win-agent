#!/usr/bin/env bash
# Called by semantic-release (@semantic-release/exec prepareCmd) with the next
# version: stamps it into the agent, which the release commit carries. The
# release files (llama-paddock-agent-setup.exe and SHA256SUMS) are built on
# Windows from the release tag, by the release workflow's setup job.
set -euo pipefail
version="$1"
cd "$(dirname "$0")/../.."

sed -i -E "s/^(const VERSION = \")[^\"]*(\";)/\1${version}\2/" paddock-agent.mjs
