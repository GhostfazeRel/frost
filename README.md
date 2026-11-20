# Frost ❄️

A lightweight, good-looking file manager for Windows (C# / WPF, .NET 10) and Linux (Avalonia): tabs, Liquid Glass UI,
preview pane, recursive search, undo/redo, ZIP, batch rename and a command palette. On Windows it also has
Explorer-compatible copy/paste, drag and drop and right-click menu.

> **Early preview:** not yet tested on real Windows. Bug reports welcome (Issues, attach `%AppData%\Frost\crash.log`).

## What's new in 0.3.0
- **Undo / Redo.** Ctrl+Z undoes your last rename, new folder/file, copy, move or batch rename; Ctrl+Y (or Ctrl+Shift+Z) redoes it. Frost checks that everything is still where it should be and refuses rather than overwrite anything. Deleted files go to the Recycle Bin: Ctrl+Z offers to open it so you can restore them from there. The last 50 actions are remembered.
- **ZIP archives.** Right-click any selection and choose "Compress to ZIP"; right-click a .zip for "Extract here" or "Extract to…". Runs in the operations panel with cancel, never overwrites (new names like "Photos (2).zip"), and refuses archives with unsafe paths. The preview pane lists a zip's contents.
- **Batch rename.** Select several items and press F2 (or right-click, Rename…) to rename them all at once: find and replace (plain or regex), prefix, suffix, numbering with `{n}` (start, step, digits), and lower/UPPER/Title case, with a live preview. Conflicts and invalid names are marked in red and block the rename; the operation is all-or-nothing (rolled back on failure), and swaps like a->b, b->a work. One Ctrl+Z undoes the whole batch.
- **Command palette and Go to folder.** Ctrl+Shift+P searches every Frost command by typing a few letters, with its shortcut shown. Ctrl+P jumps to any recent, pinned, drive or nearby folder, or paste a full path; Enter opens it here, Ctrl+Enter in a new tab.

## Install (Windows)
Download `Frost-Setup-0.3.0.exe` from the [latest release](https://github.com/GhostfazeRel/frost/releases/latest) and run it.
It installs just for you into `%LocalAppData%\Programs\Frost` (no admin rights, no .NET needed), adds a Start menu shortcut
and an entry in Settings > Apps. "Run without installing" starts Frost straight from the setup file.
Requires Windows 10/11 x64 (the acrylic glass effect needs Windows 11).
Windows SmartScreen will warn (unsigned): More info -> Run anyway.
Crashes are logged to `%AppData%\Frost\crash.log`; settings in `%AppData%\Frost\settings.json` (kept when you uninstall).

Uninstall: Settings > Apps > Frost > Uninstall.

## Default file manager (Windows)
Windows has no official switch for this, so Frost uses the same per-user trick as the Files app. It needs the installed
Frost. When you opt in (setup checkbox, the first-run question, or "…" menu > "Make Frost the default file manager"), Frost adds an
"Open in Frost" verb as the default for folders and drives and takes over Win+E / This PC, all under
`HKCU\Software\Classes` (nothing machine-wide). Special places like Recycle Bin and Control Panel still open in Explorer.

To undo: "…" menu > "Restore Explorer as default", or uninstall Frost. Both remove only Frost's own entries and put back
whatever another app (e.g. Files) had set before.

## Linux
Download `Frost-Linux-0.3.0-x64.tar.gz` from the [latest release](https://github.com/GhostfazeRel/frost/releases/latest), then:

    tar -xzf Frost-Linux-0.3.0-x64.tar.gz
    cd Frost-Linux-0.3.0-x64
    ./install.sh

It installs just for you (no root, no .NET needed) into `~/.local/share/frost`, adds Frost to your app launcher and a
`frost` command in `~/.local/bin`. The installer asks **"Make Frost your default file manager?"**; skip the question with
`./install.sh --default` or `./install.sh --no-default`. If you say no, your current file manager stays the default.
You can switch later from the "⋯" menu: "Make Frost the default file manager" / "Restore previous default file manager".
Setting the default uses `xdg-mime` (folders, `inode/directory`), so it works in any desktop that follows the
freedesktop standard (GNOME, KDE, Hyprland, niri, Sway, …).

Works with other file managers: copy/cut in Frost and paste in Nautilus, Dolphin, Thunar and others (or the other way round);
drag files onto a folder, empty space or a sidebar place (Shift moves, Ctrl copies, otherwise move on the same drive and
copy across drives). Dragging to or from other apps isn't supported yet (Avalonia on X11).

Needs: x64 Linux with X11 or XWayland, `gio` (glib2) for the trash, `xdg-utils`. Settings are in `~/.config/frost`.
Delete moves to the trash; where a drive has no trash (e.g. `/tmp`), Frost refuses and Shift+Delete deletes permanently.

Uninstall: `~/.local/share/frost/uninstall.sh` (puts your previous default file manager back; keeps your settings).

## Updates (Windows)
The installed Frost checks [GitHub releases](https://github.com/GhostfazeRel/frost/releases) at most once a day, in the background.
When a newer version is out it asks first: "Download and install", "Skip this version" or "Later". Downloads come only over HTTPS from
github.com and are checked against GitHub's SHA-256 digest when the release has one (otherwise HTTPS is the only check).
The setup then reopens with your current options. Check by hand anytime: "…" menu > "Check for updates"
(the only way for copies run without installing).

## Build
    dotnet build Frost.slnx
    dotnet test tests/Frost.Core.Tests
    dotnet publish src/Frost.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist

Linux: `dotnet test tests/Frost.Linux.Tests` (runs headless) and `packaging/linux/build.sh`, which writes
`dist/Frost-Linux-<version>-x64.tar.gz`. Run from source: `dotnet run --project src/Frost.Linux`.

The Windows release asset is that same `dist/Frost.exe` renamed to `Frost-Setup-0.3.0.exe` (a file name containing "setup", or the `--install` argument, starts the installer).

## Layout
- `src/Frost.Core` – listing, sorting, search, copy/move/delete (cross-platform, unit tested)
- `src/Frost.Shell` – Win32 interop: icons, thumbnails, native context menu, recycle bin, clipboard, acrylic
- `src/Frost.App` – WPF UI (Windows)
- `src/Frost.Linux` – Avalonia UI and Linux integration (trash via gio, xdg-open, xdg-mime)
- `packaging/linux` – Linux installer, uninstaller and release build script

## Shortcuts
Ctrl+T/W new/close tab · Ctrl+Shift+T reopen tab · Ctrl+Tab next tab · Alt+Left/Right back/forward · Alt+Up parent ·
Ctrl+L / F4 address bar · Ctrl+F / Ctrl+E search · F2 rename · Ctrl+Shift+N new folder · Ctrl+C/X/V · Del recycle ·
Shift+Del delete permanently · Alt+P preview pane · Ctrl+wheel icon size · F5 refresh ·
Ctrl+Z undo · Ctrl+Y / Ctrl+Shift+Z redo · Ctrl+Shift+P command palette · Ctrl+P go to folder (Ctrl+Enter: new tab) · F2 on several items: batch rename

## Tester checklist (never run on Windows yet – report anything odd + crash.log)
1. Launch: glass/acrylic background, rounded corners, maximize (edges not cut off), drag window by the tab strip, min/max/close buttons.
2. Details view: click headers to sort, resize columns, Ctrl/Shift+click selection, file icons correct.
3. Tiles / Large / Extra large views: scroll a big folder (e.g. C:\Windows\System32), arrow keys, Ctrl+wheel, image thumbnails (look for black or haloed edges).
4. F2 rename (selects name without extension), New folder (goes into rename), address bar typing + Ctrl+L.
5. Copy/paste within Frost and to/from Explorer (both directions, copy and cut); paste onto an existing file -> conflict dialog.
6. Drag files to/from Explorer and between tabs.
7. Right-click: Frost menu; "Show more options" and Shift+right-click -> native Windows menu (check Open with, Send to, New submenus, Properties). Right-click empty space too.
8. Delete -> Recycle Bin (restorable); Shift+Del -> permanent with confirm.
9. Search: type to filter, Enter for recursive search, Esc to cancel.
10. Sidebar: quick access, pin a folder, drives with usage bars; preview pane (Alt+P) for an image, a .txt and a folder.
11. Close and reopen: tabs, window size and view restored.
12. Install: run `Frost-Setup-0.3.0.exe`, Install with defaults -> Frost starts from `%LocalAppData%\Programs\Frost`, Start menu shortcut works, Frost listed in Settings > Apps. Run setup again while Frost is open -> friendly "close Frost" message.
13. Default file manager: tick it in setup (or accept the first-run question, or use the … menu). Double-click a folder on the desktop, open a drive in This PC, press Win+E -> all open in Frost. Recycle Bin and Control Panel still open in Explorer. An offline `\\server\share` opens Frost with a "Couldn't open" notice (no Frost/Explorer loop). With Files app set as default first, enabling then restoring Frost gives Files back folders and Win+E.
14. "Restore Explorer as default" in the … menu -> folders, drives and Win+E open in Explorer again.
15. Uninstall from Settings > Apps -> confirm dialog; shortcuts, the Apps entry and the install folder are gone, Explorer is the default again, `%AppData%\Frost` is still there.
16. Updates: with a newer `Frost-Setup-X.Y.Z.exe` release on GitHub, the installed Frost asks on start (once per 24 h); "Download and install" shows progress, Cancel stops it, then setup opens with your options pre-ticked and installs over the old copy. "Skip this version" stops the automatic prompt. "…" > "Check for updates" says "You're up to date" when it is.
17. Undo/Redo: rename a file, Ctrl+Z (name returns), Ctrl+Y (renamed again). Copy+paste a file into its own folder, Ctrl+Z (copy goes to Recycle Bin). Move a file into a folder, Ctrl+Z (back). New folder, Ctrl+Z. Delete a file, Ctrl+Z shows the "Open Recycle Bin" dialog. Create a file with the old name before undoing a rename: undo must refuse with a message.
18. ZIP: compress a folder and a multi-selection to ZIP; extract here and to another folder; cancel a big one and confirm no partial file remains; preview a .zip. Ctrl+Z after compressing moves the new zip to the Recycle Bin.
19. Batch rename: select 3+ files, press F2: the Batch Rename window opens; type a prefix `img{n}_` with 3 digits and check the preview; make two names collide and confirm red rows disable Rename; press Enter to apply, check the files; one Ctrl+Z reverts all of them; F2 on one file still edits inline.
20. Command palette: Ctrl+Shift+P, type "nt" -> New tab runs; try Undo, Compress to ZIP and Batch rename from it. Ctrl+P, type part of a recently visited folder -> Enter goes there, Ctrl+Enter opens a new tab; paste an existing path -> "Go to <path>" is first; Esc or clicking away closes the overlay.
