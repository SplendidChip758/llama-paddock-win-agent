#!/usr/bin/env bash
# Builds paddock-agent.ico, the icon of the agent, the tray app and the setup, with ImageMagick:
#   bash logo/make-icon.sh
# Up to 32 px it's mark-small.svg, the llama alone (llama-paddock's brand/logo), scaled without
# smoothing so the pixels stay sharp: the prompt and the "WINDOWS" line would be noise there.
# From 48 px it's mark.svg, this agent's mark.
set -euo pipefail
cd "$(dirname "$0")"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
for n in 16 20 24 32; do
  magick -background none -density $((72 * n / 4)) mark-small.svg -filter point -resize "${n}x${n}" "PNG32:$tmp/$n.png"
done
for n in 48 64 256; do
  magick -background none -density $((72 * n / 16)) mark.svg -resize "${n}x${n}" "PNG32:$tmp/$n.png"
done
magick "$tmp"/{16,20,24,32,48,64,256}.png paddock-agent.ico
