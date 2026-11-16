# Taskbar Monitor

A small Windows app that shows live **CPU**, **GPU**, **RAM** and **temperature** (CPU or GPU) as colour-coded ring gauges on the taskbar.

**Default: a taskbar panel** of ring gauges, just left of the clock/tray area: **CPU**, **GPU**, **RAM** and **TEMP** (CPU temperature by default; switch to GPU temperature in the menu). Each ring fills clockwise from the top with the current level, and the label sits in the middle. Turn on **Numbers inside rings** in the right-click menu to show `23%` / `61°` in the middle instead.

Rings are colour-coded by level:

| Colour | CPU / GPU / RAM | CPU temp | GPU temp |
|---|---|---|---|
| Green | under 60% | under 70 °C | under 65 °C |
| Yellow | 60–80% | 70–80 °C | 65–80 °C |
| Orange | 80–95% | 80–90 °C | 80–88 °C |
| Red | 95% and up | 90 °C and up | 88 °C and up |

The TEMP ring treats 100 °C as full. It follows your light/dark taskbar theme. Thresholds live in `Levels.cs`.

- Hover the panel for a live tooltip: CPU % and temperature, GPU % (with the busiest engine, e.g. 3D or VideoDecode) and temperature, and RAM (% and GB used / total).
- Double-click to open Task Manager.
- Right-click for the menu:
  - **Display**: *Taskbar panel* (default), *One tray icon* (a square icon with one row per metric), or *Separate tray icons* (one icon per metric; the strip along the top tells them apart: blue CPU, green GPU, purple RAM, pink TEMP)
  - **Show**: tick CPU, GPU, RAM and/or TEMP to choose what the panel or icons show (the tooltip always shows everything)
  - **Temperature**: *CPU temperature* (default) or *GPU temperature* for the TEMP ring/icon (the tooltip always shows both)
  - **Numbers inside rings**, **Size** of the rings (50 / 64 / 80 / 100%, default 64%), **Update every** 0.5 / 1 / 2 / 5 s, **Start with Windows**, **Exit**

The single tray icon is easiest to read with two metrics; with three or four, the rows get very small unless your display is scaled above 100%.

Tray icons can't be wider than a square in Windows, so the panel is a small window docked on the taskbar itself (the same approach as tools like TrafficMonitor). It supports the primary taskbar in its horizontal position. If it can't attach (vertical taskbar, or Explorer still starting up), the app shows the single tray icon instead and keeps retrying. It also re-attaches automatically if Explorer restarts.

**CPU temperature**: Windows has no built-in way for normal apps to read the real CPU sensor (tools like HWiNFO install a special driver and need admin rights). So the app uses the best source it can find:

1. **[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), if it's running.** It reads the real CPU sensors, and this app picks up its readings automatically. For accurate CPU temperature, install it, run it as administrator, and set it to start minimized with Windows (Options → *Run On Windows Startup*, *Start Minimized*, *Minimize To Tray*). OpenHardwareMonitor works too.
2. **Otherwise, the motherboard's ACPI thermal zone**, which Windows exposes without admin. It's usually reasonable on laptops, but many desktops report nothing or a fixed placeholder value. The tooltip marks it as "(estimate)".

If neither is available, the TEMP ring shows `--`. You can switch it to GPU temperature instead.

**GPU temperature** comes from the same Windows API Task Manager uses, so it needs Windows 10 2004+ and a recent driver. It shows for most dedicated NVIDIA/AMD GPUs. Many integrated GPUs don't report it, so in that case the line is simply left out. Rule of thumb: if Task Manager → Performance → GPU shows a temperature, this app will too.

Works with NVIDIA, AMD and Intel GPUs on Windows 10 (1709+) and Windows 11. It reads the same performance counters Task Manager uses, so the numbers should match it. No admin rights needed.

## Download

**[⬇ Download TaskbarMonitor.exe](https://github.com/jrosprs7/pcmonitor/releases/latest/download/TaskbarMonitor.exe)** — always the latest version. Runs on any Windows 10/11 PC; nothing to install.

All versions and the smaller `TaskbarMonitor-small.exe` (needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)) are on the [Releases page](https://github.com/jrosprs7/pcmonitor/releases).

### Automatic updates
The app checks for a new release a minute after it starts and then every 6 hours. When one is out, it downloads it, checks it, swaps itself and restarts — no clicks needed. Right-click → **Updates** shows your version, has **Check for updates now**, and lets you turn **Update automatically** off.

Keep the exe in a folder you own (e.g. `%LOCALAPPDATA%\TaskbarMonitor\`) so it can update itself; it can't replace itself inside `C:\Program Files`.

### Publishing a new version (maintainers)
```bash
git tag v1.2.0
git push origin v1.2.0
```
The **Release** workflow builds both exes on Windows, stamps them with the version, and publishes the GitHub Release. Installed copies pick it up automatically. Builds from the **Build** workflow (Actions → Artifacts) and local builds are development builds and never update themselves.

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
- `Monitors.cs` – CPU (`Processor Information\% Processor Time`, same as current Task Manager), CPU temperature (LibreHardwareMonitor WMI, else `Thermal Zone Information`), GPU (`GPU Engine\Utilization Percentage`), GPU temperature (`D3DKMTQueryAdapterInfo` adapter perf data) and RAM (`GlobalMemoryStatusEx`)
- `TaskbarWidget.cs` – the taskbar panel (ring gauges) and its hover tooltip
- `IconRenderer.cs` – draws the numbers into tray icons
- `Levels.cs` – green / yellow / orange / red thresholds
- `Fonts.cs` – the compact font (Bahnschrift SemiBold Condensed, with fallbacks)
- `TrayContext.cs` – display modes, menu, background sampling thread
- `.github/workflows/build.yml` – builds and smoke-tests the app on Windows for every push
- `.github/workflows/release.yml` – publishes a GitHub Release when a `v*` tag is pushed
- `Updater.cs` – self-update from GitHub Releases
- `Settings.cs` – settings in `HKCU\Software\TaskbarMonitor`, startup entry in `HKCU\...\Run`
