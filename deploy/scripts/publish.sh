#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$ROOT/web"
npm ci
npm run build
rm -rf "$ROOT/src/Jukebox.Host/wwwroot"
mkdir -p "$ROOT/src/Jukebox.Host/wwwroot"
cp -a "$ROOT/web/dist/." "$ROOT/src/Jukebox.Host/wwwroot/"
rm -rf "$ROOT/publish/app/wwwroot"
dotnet publish "$ROOT/src/Jukebox.Host/Jukebox.Host.csproj" -c Release -r linux-x64 --self-contained true -o "$ROOT/publish/app"
dotnet publish "$ROOT/src/Jukebox.UpdateAgent/Jukebox.UpdateAgent.csproj" -c Release -r linux-x64 --self-contained true -o "$ROOT/publish/updater"
echo "$ROOT/publish"
