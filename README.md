# GPU Crash Detector

Windows tray app for collecting GPU crash diagnostics in near real time.

It runs in the system tray, watches Windows event logs and crash-artifact folders, and writes local reports when it detects GPU-related failures such as TDRs, display driver resets, WHEA events, and related crash evidence.

## Project Layout

- `GpuCrashDetector/`
  - .NET tray monitor
  - publish and startup-task installer scripts
- `release/windows/`
  - Windows release package

## Requirements

- Windows
- .NET 10 SDK to build from source
- .NET 10 Desktop Runtime to run the packaged release

## Tray Behavior

When started, the app appears in the Windows notification area instead of opening a console window.

Tray menu actions:

- view current monitor status
- view last trigger
- view last snapshot time
- open logs folder
- open latest report
- capture a snapshot manually
- enable or disable run on login
- open the `Utilities` submenu for manual recovery actions
- exit the app

The tray menu can enable startup on login by itself. It writes a per-user Windows Run entry, so no separate installer is required for normal use.

## Utilities

The tray app includes a manual `Utilities` submenu. These actions do not run automatically.

- `Restart AMD Services`
  - discovers installed AMD-related Windows services and restarts them one by one
  - requests elevation only when you click it
- `Clean Local Reports And Logs`
  - deletes only this app's generated `monitor.log`, `gpu-diagnostic-report-*.txt`, and `dxdiag-*.txt` files
  - leaves unrelated files in the reports folder alone
- `Recover AMD Display Device`
  - discovers an AMD display adapter, enables it, restarts it, and scans for hardware changes
  - requests elevation only when you click it

## Run From Source

From `C:\Users\cfp\Documents\detector\GpuCrashDetector`:

```powershell
$env:APPDATA = Join-Path $PWD '.appdata'
$env:USERPROFILE = Join-Path $PWD '.userprofile'
$env:HOME = $env:USERPROFILE
$env:NUGET_PACKAGES = Join-Path $PWD '.nuget-packages'

dotnet restore --configfile .\NuGet.Config
dotnet build --no-restore
dotnet .\bin\Debug\net10.0-windows\GpuCrashDetector.dll --interval-seconds 5 --artifact-interval-seconds 2 --days 1
```

## Output

By default, reports are written under:

`GpuCrashDetector\bin\Debug\net10.0-windows\reports`

Generated output includes:

- `gpu-diagnostic-report-*.txt`
- `dxdiag-*.txt`
- `monitor.log`

## Run As EXE

Build output includes a Windows executable:

`GpuCrashDetector\bin\Debug\net10.0-windows\GpuCrashDetector.exe`

Example:

```powershell
.\bin\Debug\net10.0-windows\GpuCrashDetector.exe --interval-seconds 5 --artifact-interval-seconds 2 --days 1
```

## Publish Repo Release Package

From `C:\Users\cfp\Documents\detector\GpuCrashDetector`:

```powershell
.\publish-release.cmd
```

This creates a tracked Windows release package at:

`release\windows\GpuCrashDetector.exe`

You can also run the PowerShell script directly:

```powershell
.\publish-release.ps1 -Configuration Release
```

## Run The Release EXE

From `C:\Users\cfp\Documents\detector`:

```powershell
.\release\windows\GpuCrashDetector.exe --interval-seconds 5 --artifact-interval-seconds 2 --days 1
```

This starts the tray app in the background. The icon appears in the Windows notification area.

Note: the default packaged release is framework-dependent, so the PC needs the matching .NET desktop runtime already installed. On this machine that is already true because the app is being built locally.

## Build EXE In GitHub

The repo includes [build-release.yml](C:/Users/cfp/Documents/detector/.github/workflows/build-release.yml), which builds a self-contained `win-x64` executable in GitHub Actions.

What it does:

- restores and publishes the app on `windows-latest`
- creates a single-file self-contained executable package
- uploads `GpuCrashDetector-win-x64.zip` as a workflow artifact
- attaches the zip to a GitHub Release when a release is published

After the action runs, download the zip from GitHub, extract it, and run `GpuCrashDetector.exe`. Then use the tray menu option `Enable Run On Login` once if you want it to start automatically with Windows.

## Start Automatically On Login

The project includes:

- `GpuCrashDetector\install-startup-task.cmd`
- `GpuCrashDetector\install-startup-task.ps1`

They publish the app and register a current-user Windows Scheduled Task that starts the EXE at logon.

That path is optional now. The preferred flow is to launch the EXE and enable startup from the tray menu.

Simple option:

```powershell
cd C:\Users\cfp\Documents\detector\GpuCrashDetector
.\install-startup-task.cmd
```

Custom option:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-startup-task.ps1 `
  -TaskName "GpuCrashDetector" `
  -IntervalSeconds 5 `
  -ArtifactIntervalSeconds 2 `
  -Days 1
```

The startup installer now publishes to the tracked release folder first, then registers:

`release\windows\GpuCrashDetector.exe`

## Release Output

When you run the packaged EXE, generated diagnostics are written under:

`release\windows\reports`

That runtime output stays local and is ignored by Git.

## Notes

- This is a tray-style Windows app and does not open a console window in normal use.
- The preferred startup path is the tray app's built-in `Enable Run On Login` option.
- The Task Scheduler installer scripts are still available as a fallback.
- Generated reports and local environment folders are ignored by Git.
