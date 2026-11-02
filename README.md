# Taskbar Monitor

A small Windows app that shows live **CPU %** and **GPU %** (plus GPU temperature on hover) on the taskbar.

**Default: a taskbar panel.** A small rectangle sits on the taskbar just left of the clock/tray area:

```
CPU  23%
GPU  45%
```

Each row has a thin usage bar underneath, and the value turns **red at 90% or more**. It follows your light/dark taskbar theme.

- Hover the panel for a live tooltip: CPU %, GPU % (with the busiest engine, e.g. 3D or VideoDecode), and **GPU temperature**.
- Double-click to open Task Manager.
- Right-click for the menu: **Display** (see below), update interval (0.5 / 1 / 2 / 5 s), **Start with Windows**, Exit.

**Display options** (right-click → Display):
| Option | What you get |
|---|---|
| Taskbar panel (default) | One rectangular panel on the taskbar |
| One tray icon | A single square icon: CPU number on top, GPU below |
| Two tray icons | Separate blue CPU and green GPU icons |
| CPU / GPU icon only | Just one of them |

Tray icons can't be wider than a square in Windows, so the panel is a small window docked on the taskbar itself (the same approach as tools like TrafficMonitor). It supports the primary taskbar in its horizontal position. If it can't attach (vertical taskbar, or Explorer still starting up), the app shows the single combined tray icon instead and keeps retrying. It also re-attaches automatically if Explorer restarts.

**GPU temperature** comes from the same Windows API Task Manager uses, so it needs Windows 10 2004+ and a recent driver. It shows for most dedicated NVIDIA/AMD GPUs. Many integrated GPUs don't report it, so in that case the line is simply left out. Rule of thumb: if Task Manager → Performance → GPU shows a temperature, this app will too.

Works with NVIDIA, AMD and Intel GPUs on Windows 10 (1709+) and Windows 11. It reads the same performance counters Task Manager uses, so the numbers should match it. No admin rights needed.

## Run

| File | Size | Needs |
|---|---|---|
| `dist/standalone/TaskbarMonitor.exe` | ~70 MB | Nothing. Just run it. |
| `dist/small/TaskbarMonitor.exe` | ~2 MB | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |

Put the exe somewhere permanent (for example `%LOCALAPPDATA%\TaskbarMonitor\`) before you turn on *Start with Windows*, because that setting saves the exe's current path.

The exe isn't code-signed, so Windows SmartScreen may warn the first time you run it. Click **More info → Run anyway**.

### Windows 11: tray icons hidden behind the ^ arrow?
(Only applies to the tray icon display options.) Windows 11 puts new tray icons in the overflow menu. The app tries to pin itself to the taskbar the first time it runs. If the icons still don't show, open
**Settings → Personalization → Taskbar → Other system tray icons** and turn on the *TaskbarMonitor* entries
(or drag the icons from the ^ menu onto the taskbar).

## Build from source
Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), then run `build.cmd`.

## Files
- `Monitors.cs` – CPU (`Processor Information\% Processor Utility`), GPU (`GPU Engine\Utilization Percentage`) and GPU temperature (`D3DKMTQueryAdapterInfo` adapter perf data)
- `TaskbarWidget.cs` – the rectangular taskbar panel and its hover tooltip
- `IconRenderer.cs` – draws the numbers into tray icons
- `TrayContext.cs` – display modes, menu, background sampling thread
- `Settings.cs` – settings in `HKCU\Software\TaskbarMonitor`, startup entry in `HKCU\...\Run`
