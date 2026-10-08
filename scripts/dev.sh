#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
cd "$ROOT/web"
if [[ ! -d node_modules ]]; then
  npm ci
fi
npm run build
rm -rf "$ROOT/src/Jukebox.Host/wwwroot"
mkdir -p "$ROOT/src/Jukebox.Host/wwwroot"
cp -a "$ROOT/web/dist/." "$ROOT/src/Jukebox.Host/wwwroot/"
cd "$ROOT"
exec dotnet run --project src/Jukebox.Host --no-launch-profile --urls http://0.0.0.0:47123
