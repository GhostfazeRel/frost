#!/usr/bin/env bash
# Installs Frost for the current user (no root needed).
#   ./install.sh               asks whether Frost should be the default file manager
#   ./install.sh --default     install and make it the default
#   ./install.sh --no-default  install without asking
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
dest="$data/frost"
apps="$data/applications"
make_default=""
for a in "$@"; do
  case "$a" in
    --default) make_default=y ;;
    --no-default) make_default=n ;;
    *) echo "Unknown option: $a" >&2; exit 2 ;;
  esac
done

[ -x "$here/app/Frost" ] || { echo "app/Frost not found next to install.sh" >&2; exit 1; }
# Ask before frost.desktop exists: once it does, xdg-mime may already report Frost as the folder handler.
prev="$(xdg-mime query default inode/directory 2>/dev/null || true)"
mkdir -p "$dest" "$apps" "$HOME/.local/bin"
rm -rf "$dest/app"
cp -r "$here/app" "$dest/app"
cp "$here/uninstall.sh" "$dest/uninstall.sh"
ln -sf "$dest/app/Frost" "$HOME/.local/bin/frost"

cat > "$apps/frost.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Frost
GenericName=File Manager
Comment=Browse and manage your files
Exec="$dest/app/Frost" %U
Icon=system-file-manager
Terminal=false
Categories=System;FileTools;FileManager;
MimeType=inode/directory;
StartupWMClass=Frost
DESKTOP
update-desktop-database "$apps" 2>/dev/null || true
echo "Frost installed. Open it from your app launcher, or run: frost"

if [ -z "$make_default" ]; then
  if [ -t 0 ]; then
    read -rp "Make Frost your default file manager? [y/N] " r
    [[ "$r" =~ ^[Yy] ]] && make_default=y || make_default=n
  else
    make_default=n
  fi
fi

mkdir -p "${XDG_CONFIG_HOME:-$HOME/.config}" # xdg-mime silently writes nothing without it
if [ "$make_default" = y ]; then
  # Remember the first non-Frost default so uninstall.sh can put it back.
  if [ ! -f "$dest/previous-default" ] && [ "$prev" != frost.desktop ]; then
    printf '%s\n' "$prev" > "$dest/previous-default"
  fi
  xdg-mime default frost.desktop inode/directory
  echo "Frost is now your default file manager (was: ${prev:-none})."
elif [ -n "$prev" ] && [ "$prev" != frost.desktop ] && [ "$(xdg-mime query default inode/directory 2>/dev/null)" != "$prev" ]; then
  # Installing alone made Frost the fallback handler; keep the user's file manager.
  xdg-mime default "$prev" inode/directory
fi
