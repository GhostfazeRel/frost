#!/usr/bin/env bash
# Removes Frost and, if it was the default file manager, gives that back to the previous one.
set -euo pipefail
data="${XDG_DATA_HOME:-$HOME/.local/share}"
dest="$data/frost"
apps="$data/applications"

if [ "$(xdg-mime query default inode/directory 2>/dev/null || true)" = frost.desktop ]; then
  prev="$(cat "$dest/previous-default" 2>/dev/null || true)"
  if [ -n "$prev" ]; then
    xdg-mime default "$prev" inode/directory
    echo "Default file manager restored to $prev."
  else
    echo "Frost was your default file manager; pick a new one in your system settings."
  fi
fi

rm -f "$apps/frost.desktop"
[ "$(readlink "$HOME/.local/bin/frost" 2>/dev/null)" = "$dest/app/Frost" ] && rm -f "$HOME/.local/bin/frost"
update-desktop-database "$apps" 2>/dev/null || true
rm -rf "$dest"
echo "Frost removed. Your settings stay in ${XDG_CONFIG_HOME:-$HOME/.config}/frost."
