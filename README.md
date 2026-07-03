# GPU Crash Detector

Windows tray app for collecting GPU crash diagnostics in near real time.

It runs in the system tray, watches Windows event logs and crash-artifact folders, and writes local reports when it detects GPU-related failures such as TDRs, display driver resets, WHEA events, and related crash evidence.

## Project Layout

- `GpuCrashDetector/`
  - .NET console monitor
  - startup-task installer scripts

## Requirements

- Windows
- .NET 10 SDK

## Tray Behavior

When started, the app appears in the Windows notification area instead of opening a console window.

Tray menu actions:

- view current monitor status
- view last trigger
- view last snapshot time
- open logs folder
- open latest report
- capture a snapshot manually
- exit the app

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

## Publish Self-Contained EXE

From `C:\Users\cfp\Documents\detector\GpuCrashDetector`:

```powershell
$env:APPDATA = Join-Path $PWD '.appdata'
$env:USERPROFILE = Join-Path $PWD '.userprofile'
$env:HOME = $env:USERPROFILE
$env:NUGET_PACKAGES = Join-Path $PWD '.nuget-packages'

dotnet publish -c Release -r win-x64 --self-contained true
```

Published executable:

`GpuCrashDetector\bin\Release\net10.0-windows\win-x64\publish\GpuCrashDetector.exe`

## Start Automatically On Login

The project includes:

- `GpuCrashDetector\install-startup-task.cmd`
- `GpuCrashDetector\install-startup-task.ps1`

They publish the app and register a current-user Windows Scheduled Task that starts the EXE at logon.

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

## Notes

- This is a tray-style Windows app and does not open a console window in normal use.
- The startup installer uses Task Scheduler so the tray app starts automatically at login.
- Generated reports and local environment folders are ignored by Git.
