#!/usr/bin/env bash
# Builds dist/Frost-Linux-<version>-x64.tar.gz: a self-contained Frost (no .NET needed) plus install.sh.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
ver="$(grep -oP '(?<=<Version>)[^<]+' "$root/src/Frost.Linux/Frost.Linux.csproj")"
name="Frost-Linux-$ver-x64"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

dotnet publish "$root/src/Frost.Linux" -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
  -o "$stage/$name/app"
cp "$here/install.sh" "$here/uninstall.sh" "$stage/$name/"
chmod +x "$stage/$name/"*.sh
mkdir -p "$root/dist"
tar -C "$stage" -czf "$root/dist/$name.tar.gz" "$name"
echo "Built dist/$name.tar.gz"
