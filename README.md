# Frost ❄️

A lightweight, good-looking file manager for Windows (C# / WPF, .NET 10): tabs in the title bar, Liquid Glass UI,
preview pane, recursive search, and Explorer-compatible copy/paste, drag and drop and right-click menu.

> **Early preview:** not yet tested on real Windows. Bug reports welcome (Issues, attach `%AppData%\Frost\crash.log`).

## Download
Get `Frost.exe` from the [latest release](https://github.com/GhostfazeRel/frost/releases/latest).
Single self-contained exe: no install, no .NET needed. Requires Windows 10/11 x64 (the acrylic glass effect needs Windows 11).
Windows SmartScreen will warn (unsigned): More info -> Run anyway.
Crashes are logged to `%AppData%\Frost\crash.log`; settings in `%AppData%\Frost\settings.json`.

## Build
    dotnet build Frost.slnx
    dotnet test tests/Frost.Core.Tests
    dotnet publish src/Frost.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist

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
