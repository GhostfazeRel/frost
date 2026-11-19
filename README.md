# Frost ❄️

A lightweight, good-looking file manager for Windows (C# / WPF, .NET 10): tabs in the title bar, Liquid Glass UI,
preview pane, recursive search, and Explorer-compatible copy/paste, drag and drop and right-click menu.

> **Early preview:** not yet tested on real Windows. Bug reports welcome (Issues, attach `%AppData%\Frost\crash.log`).

## Install
Download `Frost-Setup-0.2.0.exe` from the [latest release](https://github.com/GhostfazeRel/frost/releases/latest) and run it.
It installs just for you into `%LocalAppData%\Programs\Frost` (no admin rights, no .NET needed), adds a Start menu shortcut
and an entry in Settings > Apps. "Run without installing" starts Frost straight from the setup file.
Requires Windows 10/11 x64 (the acrylic glass effect needs Windows 11).
Windows SmartScreen will warn (unsigned): More info -> Run anyway.
Crashes are logged to `%AppData%\Frost\crash.log`; settings in `%AppData%\Frost\settings.json` (kept when you uninstall).

Uninstall: Settings > Apps > Frost > Uninstall.

## Default file manager
Windows has no official switch for this, so Frost uses the same per-user trick as the Files app. When you opt in
(setup checkbox, the first-run question, or "…" menu > "Make Frost the default file manager"), Frost adds an
"Open in Frost" verb as the default for folders and drives and takes over Win+E / This PC, all under
`HKCU\Software\Classes` (nothing machine-wide). Special places like Recycle Bin and Control Panel still open in Explorer.

To undo: "…" menu > "Restore Explorer as default", or uninstall Frost. Both remove only the keys Frost added.

## Build
    dotnet build Frost.slnx
    dotnet test tests/Frost.Core.Tests
    dotnet publish src/Frost.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist

The release asset is that same `dist/Frost.exe` renamed to `Frost-Setup-0.2.0.exe` (a file name containing "setup", or the `--install` argument, starts the installer).

## Layout
- `src/Frost.Core` – listing, sorting, search, copy/move/delete (cross-platform, unit tested)
- `src/Frost.Shell` – Win32 interop: icons, thumbnails, native context menu, recycle bin, clipboard, acrylic
- `src/Frost.App` – WPF UI

## Shortcuts
Ctrl+T/W new/close tab · Ctrl+Shift+T reopen tab · Ctrl+Tab next tab · Alt+Left/Right back/forward · Alt+Up parent ·
Ctrl+L / F4 address bar · Ctrl+F / Ctrl+E search · F2 rename · Ctrl+Shift+N new folder · Ctrl+C/X/V · Del recycle ·
Shift+Del delete permanently · Alt+P preview pane · Ctrl+wheel icon size · F5 refresh

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
12. Install: run `Frost-Setup-0.2.0.exe`, Install with defaults -> Frost starts from `%LocalAppData%\Programs\Frost`, Start menu shortcut works, Frost listed in Settings > Apps. Run setup again while Frost is open -> friendly "close Frost" message.
13. Default file manager: tick it in setup (or accept the first-run question, or use the … menu). Double-click a folder on the desktop, open a drive in This PC, press Win+E -> all open in Frost. Recycle Bin and Control Panel still open in Explorer.
14. "Restore Explorer as default" in the … menu -> folders, drives and Win+E open in Explorer again.
15. Uninstall from Settings > Apps -> confirm dialog; shortcuts, the Apps entry and the install folder are gone, Explorer is the default again, `%AppData%\Frost` is still there.
