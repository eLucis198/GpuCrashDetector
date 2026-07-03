using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using System.Globalization;

namespace GpuCrashDetector;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 1;
        }

        var options = AppOptions.Parse(args);
        Directory.CreateDirectory(options.OutputDirectory);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var appContext = new TrayApplicationContext(options);
        Application.Run(appContext);
        return 0;
    }

    internal static DiagnosticReport CollectDiagnosticReport(AppOptions options, string triggerReason)
    {
        var report = new DiagnosticReport();
        report.Add("Run Context", BuildRunContext(options, triggerReason));
        report.Add("System Overview", RunPowerShell("""
            $os = Get-CimInstance Win32_OperatingSystem
            $cs = Get-CimInstance Win32_ComputerSystem
            [pscustomobject]@{
              ComputerName = $env:COMPUTERNAME
              Manufacturer = $cs.Manufacturer
              Model = $cs.Model
              TotalMemoryGB = [math]::Round($cs.TotalPhysicalMemory / 1GB, 2)
              OS = $os.Caption
              Version = $os.Version
              Build = $os.BuildNumber
              LastBootUpTime = $os.LastBootUpTime
            } | Format-List
            """));
        report.Add("GPU Adapters", RunPowerShell("""
            Get-CimInstance Win32_VideoController |
              Select-Object Name, AdapterCompatibility, DriverVersion, DriverDate, VideoProcessor, AdapterRAM, VideoModeDescription, Status, PNPDeviceID |
              Format-List
            """));
        report.Add("Display Drivers", RunPowerShell("""
            Get-CimInstance Win32_PnPSignedDriver |
              Where-Object { $_.DeviceClass -eq 'DISPLAY' } |
              Select-Object DeviceName, Manufacturer, DriverVersion, DriverDate, DriverProviderName, InfName, IsSigned |
              Sort-Object DeviceName |
              Format-List
            """));
        report.Add("Plug and Play Display Devices", RunPowerShell("""
            Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
              Select-Object Status, Class, FriendlyName, InstanceId, Problem, Present |
              Format-List
            """));
        report.Add("Recent GPU-Related System Events", RunPowerShell(BuildGpuEventCommand("System", options.Days, 80)));
        report.Add("Recent GPU-Related Application Events", RunPowerShell(BuildGpuEventCommand("Application", options.Days, 50)));
        report.Add("Recent WHEA Events", RunPowerShell(BuildWheaCommand(options.Days, 40)));
        report.Add("Crash Artifacts", RunPowerShell(BuildCrashArtifactCommand()));
        report.Add("Display Registry TDR Keys", RunPowerShell("""
            $path = 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers'
            if (Test-Path $path) {
              Get-ItemProperty -Path $path |
                Select-Object TdrDelay, TdrDdiDelay, TdrLevel, TdrLimitCount, TdrLimitTime |
                Format-List
            }
            else {
              'GraphicsDrivers registry key was not found.'
            }
            """));

        var dxDiagPath = Path.Combine(options.OutputDirectory, $"dxdiag-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        report.Add("DirectX Diagnostic", RunDxDiag(dxDiagPath));
        report.DxDiagPath = dxDiagPath;
        return report;
    }

    private static string BuildRunContext(AppOptions options, string triggerReason)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Timestamp: {DateTimeOffset.Now:O}");
        builder.AppendLine($"Machine: {Environment.MachineName}");
        builder.AppendLine($"User: {Environment.UserDomainName}\\{Environment.UserName}");
        builder.AppendLine($"OS Architecture: {Environment.OSVersion}");
        builder.AppendLine($"Process Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"Lookback Days: {options.Days}");
        builder.AppendLine($"Poll Interval Seconds: {options.PollIntervalSeconds}");
        builder.AppendLine($"Trigger Reason: {triggerReason}");
        builder.AppendLine($"Output Directory: {options.OutputDirectory}");
        return builder.ToString().TrimEnd();
    }

    private static string BuildGpuEventCommand(string logName, int days, int maxEvents)
    {
        var escapedLogName = logName.Replace("'", "''");
        return $@"
$start = (Get-Date).AddDays(-{days})
$providers = @(
  'Display',
  'nvlddmkm',
  'amdkmdag',
  'amdkmdap',
  'IntelGFX',
  'igfx',
  'Application Error',
  'Windows Error Reporting',
  'Microsoft-Windows-DxgKrnl',
  'Microsoft-Windows-Dwm-Core',
  'Microsoft-Windows-WHEA-Logger'
)
$keywords = 'display driver|stopped responding|recovered|TDR|timeout detection|LiveKernelEvent|graphics|GPU'
Get-WinEvent -FilterHashtable @{{ LogName = '{escapedLogName}'; StartTime = $start }} -ErrorAction SilentlyContinue |
  Where-Object {{
    $providers -contains $_.ProviderName -or
    $_.Id -in 4101, 14, 117, 141, 142, 1, 1000, 1001 -or
    ($_.Message -and $_.Message -match $keywords)
  }} |
  Select-Object -First {maxEvents} TimeCreated, Id, LevelDisplayName, ProviderName, MachineName, Message |
  Format-List
";
    }

    private static string BuildWheaCommand(int days, int maxEvents)
    {
        return $@"
$start = (Get-Date).AddDays(-{days})
Get-WinEvent -FilterHashtable @{{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WHEA-Logger'; StartTime = $start }} -ErrorAction SilentlyContinue |
  Select-Object -First {maxEvents} TimeCreated, Id, LevelDisplayName, ProviderName, Message |
  Format-List
";
    }

    private static string BuildCrashArtifactCommand()
    {
        return """
            $paths = @(
              "$env:SystemRoot\LiveKernelReports",
              "$env:SystemRoot\Minidump",
              "$env:ProgramData\Microsoft\Windows\WER\ReportArchive",
              "$env:ProgramData\Microsoft\Windows\WER\ReportQueue"
            )
            foreach ($path in $paths) {
              "Path: $path"
              if (Test-Path $path) {
                Get-ChildItem -Path $path -Recurse -File -ErrorAction SilentlyContinue |
                  Sort-Object LastWriteTime -Descending |
                  Select-Object -First 25 FullName, Length, LastWriteTime
              }
              else {
                '  Not found.'
              }
              ''
            }
            """;
    }

    internal static string RunPowerShell(string script)
    {
        return RunProcess("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{EscapeForPowerShellArgument(script)}\"");
    }

    private static string RunDxDiag(string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var result = RunProcess("dxdiag", $"/dontskip /t \"{outputPath}\"");
        if (!File.Exists(outputPath))
        {
            return $"{result}{Environment.NewLine}dxdiag output file was not created.";
        }

        var preview = File.ReadLines(outputPath).Take(80);
        return $"{result}{Environment.NewLine}dxdiag report saved to:{Environment.NewLine}{outputPath}{Environment.NewLine}{Environment.NewLine}Preview:{Environment.NewLine}{string.Join(Environment.NewLine, preview)}";
    }

    private static string RunProcess(string fileName, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return $"Failed to start process: {fileName}";
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            var builder = new StringBuilder();
            builder.AppendLine($"Command: {fileName} {arguments}");
            builder.AppendLine($"ExitCode: {process.ExitCode}");

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                builder.AppendLine();
                builder.AppendLine(stdout.TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                builder.AppendLine();
                builder.AppendLine("stderr:");
                builder.AppendLine(stderr.TrimEnd());
            }

            return builder.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Command failed: {fileName} {arguments}{Environment.NewLine}{ex}";
        }
    }

    private static string EscapeForPowerShellArgument(string value)
    {
        return value
            .Replace("`", "``", StringComparison.Ordinal)
            .Replace("\"", "`\"", StringComparison.Ordinal)
            .Replace(Environment.NewLine, " ", StringComparison.Ordinal);
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppOptions _options;
    private readonly MonitorStatus _status = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _stateItem;
    private readonly ToolStripMenuItem _lastTriggerItem;
    private readonly ToolStripMenuItem _lastSnapshotItem;
    private readonly ToolStripMenuItem _logsPathItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly SynchronizationContext _uiContext;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly GpuMonitor _monitor;
    private readonly Task _monitorTask;

    public TrayApplicationContext(AppOptions options)
    {
        _options = options;
        _monitor = new GpuMonitor(_options, _status);
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _stateItem = new ToolStripMenuItem();
        _lastTriggerItem = new ToolStripMenuItem();
        _lastSnapshotItem = new ToolStripMenuItem();
        _logsPathItem = new ToolStripMenuItem();
        _startupItem = new ToolStripMenuItem();
        var utilitiesMenu = new ToolStripMenuItem("Utilities");
        utilitiesMenu.DropDownItems.Add("Restart AMD Services", null, (_, _) => RestartAmdServices());
        utilitiesMenu.DropDownItems.Add("Clean Local Reports And Logs", null, (_, _) => CleanLocalReportsAndLogs());
        utilitiesMenu.DropDownItems.Add("Recover AMD Display Device", null, (_, _) => RecoverAmdDisplayDevice());
        var driverUtilitiesMenu = new ToolStripMenuItem("Driver Utilities");
        driverUtilitiesMenu.DropDownItems.Add("List AMD Drivers", null, (_, _) => ListAmdDrivers());
        driverUtilitiesMenu.DropDownItems.Add("Switch AMD Driver", null, (_, _) => SwitchAmdDriver());
        driverUtilitiesMenu.DropDownItems.Add("Delete AMD Driver Package", null, (_, _) => DeleteAmdDriverPackage());

        _menu = new ContextMenuStrip();
        _menu.Opening += (_, _) => RefreshMenu();
        _menu.Items.AddRange(
        [
            _stateItem,
            _lastTriggerItem,
            _lastSnapshotItem,
            _logsPathItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open Logs Folder", null, (_, _) => OpenLogsFolder()),
            new ToolStripMenuItem("Open Latest Report", null, (_, _) => OpenLatestReport()),
            new ToolStripMenuItem("Capture Snapshot Now", null, (_, _) => TriggerManualSnapshot()),
            _startupItem,
            utilitiesMenu,
            driverUtilitiesMenu,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication())
        ]);

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Visible = true,
            ContextMenuStrip = _menu,
            Text = "GPU Crash Detector"
        };
        _notifyIcon.DoubleClick += (_, _) => OpenLogsFolder();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _refreshTimer.Tick += (_, _) => RefreshMenu();
        _refreshTimer.Start();

        RefreshMenu();
        ShowBalloon("GPU Crash Detector is running", ToolTipIcon.Info);

        _monitorTask = Task.Run(() => _monitor.Run(_cancellation.Token), _cancellation.Token);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _menu.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _cancellation.Dispose();
        }

        base.Dispose(disposing);
    }

    private void RefreshMenu()
    {
        var snapshot = _status.CreateSnapshot();
        _stateItem.Text = $"Status: {snapshot.State}";
        _stateItem.Enabled = false;
        _lastTriggerItem.Text = $"Last Trigger: {Truncate(snapshot.LastTrigger ?? "none", 80)}";
        _lastTriggerItem.Enabled = false;
        _lastSnapshotItem.Text = $"Last Snapshot: {FormatTime(snapshot.LastSnapshotAt)}";
        _lastSnapshotItem.Enabled = false;
        _logsPathItem.Text = $"Logs: {Truncate(_options.OutputDirectory, 80)}";
        _logsPathItem.Enabled = false;
        _startupItem.Text = StartupRegistration.IsEnabled(_options)
            ? "Disable Run On Login"
            : "Enable Run On Login";
        _startupItem.Enabled = true;
        _startupItem.Click -= ToggleStartupRegistration;
        _startupItem.Click += ToggleStartupRegistration;

        _notifyIcon.Icon = snapshot.HasRecentError ? SystemIcons.Error : snapshot.HasRecentSnapshot ? SystemIcons.Warning : SystemIcons.Information;
        _notifyIcon.Text = BuildTrayText(snapshot);
    }

    private void OpenLogsFolder()
    {
        Directory.CreateDirectory(_options.OutputDirectory);
        OpenPath(_options.OutputDirectory);
    }

    private void OpenLatestReport()
    {
        var snapshot = _status.CreateSnapshot();
        if (!string.IsNullOrWhiteSpace(snapshot.LastReportPath) && File.Exists(snapshot.LastReportPath))
        {
            OpenPath(snapshot.LastReportPath);
            return;
        }

        var latest = Directory.Exists(_options.OutputDirectory)
            ? Directory.GetFiles(_options.OutputDirectory, "gpu-diagnostic-report-*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        if (!string.IsNullOrWhiteSpace(latest))
        {
            OpenPath(latest);
            return;
        }

        ShowBalloon("No report has been generated yet.", ToolTipIcon.Info);
    }

    private void TriggerManualSnapshot()
    {
        Task.Run(() =>
        {
            try
            {
                _monitor.RequestManualSnapshot("manual snapshot from tray menu");
                ShowBalloon("Manual snapshot requested.", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                _status.RecordError(ex);
                ShowBalloon("Manual snapshot request failed.", ToolTipIcon.Error);
            }
        });
    }

    private void RestartAmdServices()
    {
        if (!ConfirmAction("Restart AMD Services", "Restart AMD-related Windows services now?"))
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var services = ServiceDiscovery.GetAmdServices()
                    .OrderBy(service => service.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(service => service.ServiceName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (services.Length == 0)
                {
                    AppLog.Append(_options.OutputDirectory, "Restart AMD Services: no matching services found.");
                    ShowResult("Restart AMD Services", "No AMD-related Windows services were found.", ToolTipIcon.Info);
                    return;
                }

                var script = UtilityScriptBuilder.BuildRestartServicesScript(services);
                var result = ElevatedCommandRunner.Run("Restart AMD Services", script, _options.OutputDirectory);

                AppLog.Append(_options.OutputDirectory, $"Restart AMD Services: {result.Summary}");
                ShowResult("Restart AMD Services", result.Summary, result.Success ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }
            catch (Exception ex)
            {
                _status.RecordError(ex);
                AppLog.Append(_options.OutputDirectory, $"Restart AMD Services failed unexpectedly: {ex}");
                ShowResult("Restart AMD Services", "The utility failed before the command could run.", ToolTipIcon.Error);
            }
        });
    }

    private void CleanLocalReportsAndLogs()
    {
        if (!ConfirmAction("Clean Local Reports And Logs", "Delete generated monitor logs and reports from this app's output folder?"))
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(_options.OutputDirectory);
                var deletedFiles = new List<string>();
                deletedFiles.AddRange(DeleteIfExists(Path.Combine(_options.OutputDirectory, "monitor.log")));
                deletedFiles.AddRange(DeleteByPattern("gpu-diagnostic-report-*.txt"));
                deletedFiles.AddRange(DeleteByPattern("dxdiag-*.txt"));

                var summary = deletedFiles.Count == 0
                    ? "No generated log or report files were found."
                    : $"Deleted {deletedFiles.Count} generated file(s) from '{_options.OutputDirectory}'.";

                AppLog.Append(_options.OutputDirectory, $"Clean Local Reports And Logs: {summary}");
                ShowResult("Clean Local Reports And Logs", summary, ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                _status.RecordError(ex);
                AppLog.Append(_options.OutputDirectory, $"Clean Local Reports And Logs failed unexpectedly: {ex}");
                ShowResult("Clean Local Reports And Logs", "The cleanup failed unexpectedly.", ToolTipIcon.Error);
            }
        });
    }

    private void RecoverAmdDisplayDevice()
    {
        if (!ConfirmAction("Recover AMD Display Device", "Enable and restart the detected AMD display device now?"))
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var devices = DeviceDiscovery.GetAmdDisplayDevices();
                var targetDevice = devices.Count switch
                {
                    0 => null,
                    1 => devices[0],
                    _ => devices.FirstOrDefault(device => device.Present) ?? devices[0]
                };

                if (targetDevice is null)
                {
                    AppLog.Append(_options.OutputDirectory, "Recover AMD Display Device: no AMD display device found.");
                    ShowResult("Recover AMD Display Device", "No AMD display device was found.", ToolTipIcon.Info);
                    return;
                }

                var script = UtilityScriptBuilder.BuildRecoverDisplayDeviceScript(targetDevice.InstanceId);
                var result = ElevatedCommandRunner.Run("Recover AMD Display Device", script, _options.OutputDirectory);

                AppLog.Append(_options.OutputDirectory, $"Recover AMD Display Device: {result.Summary}");
                ShowResult("Recover AMD Display Device", result.Summary, result.Success ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }
            catch (Exception ex)
            {
                _status.RecordError(ex);
                AppLog.Append(_options.OutputDirectory, $"Recover AMD Display Device failed unexpectedly: {ex}");
                ShowResult("Recover AMD Display Device", "The utility failed before the command could run.", ToolTipIcon.Error);
            }
        });
    }

    private void ListAmdDrivers()
    {
        Task.Run(() =>
        {
            try
            {
                var inventory = DriverDiscovery.GetAmdDriverInventory();
                var reportPath = DriverInventoryReportWriter.WriteReport(_options.OutputDirectory, inventory);
                var summary = DriverInventoryReportWriter.BuildListSummary(inventory, reportPath);

                AppLog.Append(_options.OutputDirectory, $"List AMD Drivers: {summary}");
                ShowResult("List AMD Drivers", summary, ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                _status.RecordError(ex);
                AppLog.Append(_options.OutputDirectory, $"List AMD Drivers failed unexpectedly: {ex}");
                ShowResult("List AMD Drivers", "The driver inventory utility failed unexpectedly.", ToolTipIcon.Error);
            }
        });
    }

    private void SwitchAmdDriver()
    {
        Task.Run(() =>
        {
            try
            {
                var inventory = DriverDiscovery.GetAmdDriverInventory();
                if (inventory.TargetDevice is null)
                {
                    AppLog.Append(_options.OutputDirectory, "Switch AMD Driver: no AMD display device found.");
                    ShowResult("Switch AMD Driver", "No AMD display device was found.", ToolTipIcon.Info);
                    return;
                }

                if (!inventory.HasConfirmedActivePackage)
                {
                    AppLog.Append(_options.OutputDirectory, "Switch AMD Driver: active package correlation was ambiguous.");
                    ShowResult("Switch AMD Driver", "The active AMD driver package could not be determined confidently. Use 'List AMD Drivers' first.", ToolTipIcon.Info);
                    return;
                }

                var candidates = inventory.Packages
                    .Where(package => !package.IsActive)
                    .OrderByDescending(package => package.DriverDateSortKey)
                    .ThenByDescending(package => package.DriverVersionSortKey, DriverPackageVersionComparer.Instance)
                    .ThenBy(package => package.PublishedName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (candidates.Length == 0)
                {
                    AppLog.Append(_options.OutputDirectory, "Switch AMD Driver: no alternate AMD packages found.");
                    ShowResult("Switch AMD Driver", "No alternate installed AMD driver package was found.", ToolTipIcon.Info);
                    return;
                }

                var selected = candidates.Length == 1
                    ? candidates[0]
                    : PromptForDriverPackageSelection(
                        "Switch AMD Driver",
                        "Select the installed AMD driver package to apply:",
                        candidates);

                if (selected is null)
                {
                    AppLog.Append(_options.OutputDirectory, "Switch AMD Driver: package selection cancelled.");
                    ShowResult("Switch AMD Driver", "Driver switch was cancelled.", ToolTipIcon.Info);
                    return;
                }

                if (!ConfirmAction(
                        "Switch AMD Driver",
                        $"Switch the AMD display device to '{selected.PublishedName}' ({selected.DisplayVersionAndDate}) now?"))
                {
                    AppLog.Append(_options.OutputDirectory, $"Switch AMD Driver: confirmation declined for {selected.PublishedName}.");
                    ShowResult("Switch AMD Driver", "Driver switch was cancelled.", ToolTipIcon.Info);
                    return;
                }

                var script = UtilityScriptBuilder.BuildSwitchDriverScript(selected.PublishedName, selected.OriginalName, inventory.TargetDevice.InstanceId);
                var result = ElevatedCommandRunner.Run("Switch AMD Driver", script, _options.OutputDirectory);
                var refreshedInventory = DriverDiscovery.GetAmdDriverInventory();
                var reportPath = DriverInventoryReportWriter.WriteReport(_options.OutputDirectory, refreshedInventory);
                var summary = DriverInventoryReportWriter.BuildSwitchSummary(selected, refreshedInventory, reportPath, result);

                AppLog.Append(_options.OutputDirectory, $"Switch AMD Driver: {summary}");
                ShowResult("Switch AMD Driver", summary, ToUtilityIcon(result));
            }
            catch (Exception ex)
            {
                _status.RecordError(ex);
                AppLog.Append(_options.OutputDirectory, $"Switch AMD Driver failed unexpectedly: {ex}");
                ShowResult("Switch AMD Driver", "The driver switch utility failed unexpectedly.", ToolTipIcon.Error);
            }
        });
    }

    private void DeleteAmdDriverPackage()
    {
        Task.Run(() =>
        {
            try
            {
                var inventory = DriverDiscovery.GetAmdDriverInventory();
                if (inventory.TargetDevice is null)
                {
                    AppLog.Append(_options.OutputDirectory, "Delete AMD Driver Package: no AMD display device found.");
                    ShowResult("Delete AMD Driver Package", "No AMD display device was found.", ToolTipIcon.Info);
                    return;
                }

                if (inventory.ActivePackageCorrelationAmbiguous)
                {
                    AppLog.Append(_options.OutputDirectory, "Delete AMD Driver Package: active package correlation was ambiguous.");
                    ShowResult("Delete AMD Driver Package", "The active AMD driver package could not be determined confidently, so deletion is blocked.", ToolTipIcon.Info);
                    return;
                }

                var candidates = inventory.Packages
                    .Where(package => !package.IsActive && !package.CouldBeActive)
                    .OrderByDescending(package => package.DriverDateSortKey)
                    .ThenByDescending(package => package.DriverVersionSortKey, DriverPackageVersionComparer.Instance)
                    .ThenBy(package => package.PublishedName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (candidates.Length == 0)
                {
                    AppLog.Append(_options.OutputDirectory, "Delete AMD Driver Package: no removable AMD packages found.");
                    ShowResult("Delete AMD Driver Package", "No removable non-active AMD driver package was found.", ToolTipIcon.Info);
                    return;
                }

                var selected = candidates.Length == 1
                    ? candidates[0]
                    : PromptForDriverPackageSelection(
                        "Delete AMD Driver Package",
                        "Select the non-active AMD driver package to remove:",
                        candidates);

                if (selected is null)
                {
                    AppLog.Append(_options.OutputDirectory, "Delete AMD Driver Package: package selection cancelled.");
                    ShowResult("Delete AMD Driver Package", "Driver package deletion was cancelled.", ToolTipIcon.Info);
                    return;
                }

                if (!ConfirmAction(
                        "Delete AMD Driver Package",
                        $"Delete AMD driver package '{selected.PublishedName}' ({selected.DisplayVersionAndDate}) now?{Environment.NewLine}{Environment.NewLine}Warning: deleting display driver packages can force fallback or reinstall behavior."))
                {
                    AppLog.Append(_options.OutputDirectory, $"Delete AMD Driver Package: confirmation declined for {selected.PublishedName}.");
                    ShowResult("Delete AMD Driver Package", "Driver package deletion was cancelled.", ToolTipIcon.Info);
                    return;
                }

                var script = UtilityScriptBuilder.BuildDeleteDriverPackageScript(selected.PublishedName);
                var result = ElevatedCommandRunner.Run("Delete AMD Driver Package", script, _options.OutputDirectory);
                var refreshedInventory = DriverDiscovery.GetAmdDriverInventory();
                var reportPath = DriverInventoryReportWriter.WriteReport(_options.OutputDirectory, refreshedInventory);
                var summary = DriverInventoryReportWriter.BuildDeleteSummary(selected, refreshedInventory, reportPath, result);

                AppLog.Append(_options.OutputDirectory, $"Delete AMD Driver Package: {summary}");
                ShowResult("Delete AMD Driver Package", summary, ToUtilityIcon(result));
            }
            catch (Exception ex)
            {
                _status.RecordError(ex);
                AppLog.Append(_options.OutputDirectory, $"Delete AMD Driver Package failed unexpectedly: {ex}");
                ShowResult("Delete AMD Driver Package", "The driver package deletion utility failed unexpectedly.", ToolTipIcon.Error);
            }
        });
    }

    private void ToggleStartupRegistration(object? sender, EventArgs e)
    {
        try
        {
            if (StartupRegistration.IsEnabled(_options))
            {
                StartupRegistration.Disable(_options);
                ShowBalloon("Run on login disabled.", ToolTipIcon.Info);
            }
            else
            {
                StartupRegistration.Enable(_options);
                ShowBalloon("Run on login enabled.", ToolTipIcon.Info);
            }

            RefreshMenu();
        }
        catch (Exception ex)
        {
            _status.RecordError(ex);
            ShowBalloon("Failed to update run on login.", ToolTipIcon.Error);
        }
    }

    private void ExitApplication()
    {
        _refreshTimer.Stop();
        _notifyIcon.Visible = false;
        _cancellation.Cancel();

        try
        {
            _monitorTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        ExitThread();
    }

    private void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void ShowBalloon(string message, ToolTipIcon icon)
    {
        _notifyIcon.BalloonTipTitle = "GPU Crash Detector";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(3000);
    }

    private bool ConfirmAction(string title, string message)
    {
        return MessageBox.Show(
            message,
            title,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private IEnumerable<string> DeleteIfExists(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        File.Delete(path);
        return [path];
    }

    private IEnumerable<string> DeleteByPattern(string pattern)
    {
        return Directory.Exists(_options.OutputDirectory)
            ? Directory.GetFiles(_options.OutputDirectory, pattern)
                .Select(path =>
                {
                    File.Delete(path);
                    return path;
                })
                .ToArray()
            : [];
    }

    private void ShowResult(string title, string message, ToolTipIcon icon)
    {
        PostToUi(() =>
        {
            ShowBalloon(message, icon);
            MessageBox.Show(message, title, MessageBoxButtons.OK, ToMessageBoxIcon(icon));
        });
    }

    private void PostToUi(Action action)
    {
        _uiContext.Post(_ => action(), null);
    }

    private static MessageBoxIcon ToMessageBoxIcon(ToolTipIcon icon)
    {
        return icon switch
        {
            ToolTipIcon.Error => MessageBoxIcon.Error,
            ToolTipIcon.Warning => MessageBoxIcon.Warning,
            _ => MessageBoxIcon.Information
        };
    }

    private DriverPackageInfo? PromptForDriverPackageSelection(string title, string prompt, IReadOnlyList<DriverPackageInfo> packages)
    {
        DriverPackageInfo? selected = null;
        using var waitHandle = new ManualResetEventSlim(false);

        PostToUi(() =>
        {
            try
            {
                selected = DriverPackageSelectionDialog.Select(title, prompt, packages);
            }
            finally
            {
                waitHandle.Set();
            }
        });

        waitHandle.Wait();
        return selected;
    }

    private static ToolTipIcon ToUtilityIcon(UtilityExecutionResult result)
    {
        if (result.Success)
        {
            return ToolTipIcon.Info;
        }

        return result.Summary.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
            ? ToolTipIcon.Info
            : ToolTipIcon.Warning;
    }

    private static string BuildTrayText(MonitorStatusSnapshot snapshot)
    {
        var text = snapshot.HasRecentError
            ? $"GPU Crash Detector - Error - {snapshot.State}"
            : snapshot.HasRecentSnapshot
                ? $"GPU Crash Detector - Snapshot captured - {snapshot.State}"
                : $"GPU Crash Detector - {snapshot.State}";

        return Truncate(text, 63);
    }

    private static string FormatTime(DateTimeOffset? value)
    {
        return value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "none";
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..(maxLength - 3)] + "...";
    }
}

internal sealed class DiagnosticReport
{
    private readonly List<ReportSection> _sections = [];
    public string? DxDiagPath { get; set; }

    public void Add(string title, string content)
    {
        _sections.Add(new ReportSection(title, string.IsNullOrWhiteSpace(content) ? "No data returned." : content.TrimEnd()));
    }

    public string Render()
    {
        var builder = new StringBuilder();
        builder.AppendLine("GPU Crash Diagnostic Report");
        builder.AppendLine(new string('=', 80));

        foreach (var section in _sections)
        {
            builder.AppendLine();
            builder.AppendLine(section.Title);
            builder.AppendLine(new string('-', 80));
            builder.AppendLine(section.Content);
        }

        return builder.ToString();
    }

    public string RenderSummary(string reportPath, string dxDiagPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("GPU diagnostic collection completed.");
        builder.AppendLine($"Report: {reportPath}");
        builder.AppendLine($"DxDiag: {dxDiagPath}");
        builder.AppendLine();
        builder.AppendLine("Collected sections:");

        foreach (var section in _sections)
        {
            builder.AppendLine($"- {section.Title}");
        }

        builder.AppendLine();
        builder.AppendLine("Open the saved report and search for these terms first:");
        builder.AppendLine("- 4101");
        builder.AppendLine("- LiveKernelEvent");
        builder.AppendLine("- WHEA");
        builder.AppendLine("- nvlddmkm");
        builder.AppendLine("- amdkmdag");
        builder.AppendLine("- dxgkrnl");

        return builder.ToString().TrimEnd();
    }
}

internal sealed record ReportSection(string Title, string Content);

internal sealed class AppOptions
{
    public const string StartupValueName = "GpuCrashDetector";
    public int Days { get; private init; } = 7;
    public int PollIntervalSeconds { get; private init; } = 30;
    public int ArtifactPollIntervalSeconds { get; private init; } = 5;
    public bool CaptureBaselineOnStart { get; private init; } = true;
    public string OutputDirectory { get; private init; } = Path.Combine(AppContext.BaseDirectory, "reports");

    public static AppOptions Parse(string[] args)
    {
        var days = 7;
        var pollIntervalSeconds = 30;
        var artifactPollIntervalSeconds = 5;
        var captureBaselineOnStart = true;
        string? outputDirectory = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--days" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedDays) && parsedDays > 0:
                    days = parsedDays;
                    i++;
                    break;

                case "--out" when i + 1 < args.Length:
                    outputDirectory = args[i + 1];
                    i++;
                    break;

                case "--interval-seconds" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedInterval) && parsedInterval >= 5:
                    pollIntervalSeconds = parsedInterval;
                    i++;
                    break;

                case "--artifact-interval-seconds" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedArtifactInterval) && parsedArtifactInterval >= 2:
                    artifactPollIntervalSeconds = parsedArtifactInterval;
                    i++;
                    break;

                case "--no-baseline":
                    captureBaselineOnStart = false;
                    break;
            }
        }

        return new AppOptions
        {
            Days = days,
            PollIntervalSeconds = pollIntervalSeconds,
            ArtifactPollIntervalSeconds = artifactPollIntervalSeconds,
            CaptureBaselineOnStart = captureBaselineOnStart,
            OutputDirectory = Path.GetFullPath(outputDirectory ?? Path.Combine(AppContext.BaseDirectory, "reports"))
        };
    }

    public string BuildStartupArguments()
    {
        var arguments = new List<string>
        {
            "--interval-seconds", PollIntervalSeconds.ToString(),
            "--artifact-interval-seconds", ArtifactPollIntervalSeconds.ToString(),
            "--days", Days.ToString()
        };

        if (!CaptureBaselineOnStart)
        {
            arguments.Add("--no-baseline");
        }

        if (!string.Equals(OutputDirectory, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "reports")), StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("--out");
            arguments.Add(OutputDirectory);
        }

        return string.Join(" ", arguments.Select(QuoteArgument));
    }

    private static string QuoteArgument(string value)
    {
        return value.Contains(' ', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }
}

internal static class AppLog
{
    public static void Append(string outputDirectory, string message)
    {
        Directory.CreateDirectory(outputDirectory);
        var monitorLogPath = Path.Combine(outputDirectory, "monitor.log");
        File.AppendAllText(
            monitorLogPath,
            $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled(AppOptions options)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(AppOptions.StartupValueName) as string;
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(GetExecutablePath(), StringComparison.OrdinalIgnoreCase);
    }

    public static void Enable(AppOptions options)
    {
        var command = BuildStartupCommand(options);

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(AppOptions.StartupValueName, command, RegistryValueKind.String);
    }

    public static void Disable(AppOptions options)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(AppOptions.StartupValueName, throwOnMissingValue: false);
    }

    private static string BuildStartupCommand(AppOptions options)
    {
        return $"{QuoteExecutable(GetExecutablePath())} {options.BuildStartupArguments()}".Trim();
    }

    private static string GetExecutablePath()
    {
        return Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Could not determine the current executable path.");
    }

    private static string QuoteExecutable(string value)
    {
        return $"\"{value}\"";
    }
}

internal static class ElevatedCommandRunner
{
    public static UtilityExecutionResult Run(string actionName, string scriptContent, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var baseName = $"{SanitizeFileName(actionName)}-{Guid.NewGuid():N}";
        var scriptPath = Path.Combine(outputDirectory, $"{baseName}.payload.ps1");
        var wrapperPath = Path.Combine(outputDirectory, $"{baseName}.runner.ps1");
        var resultPath = Path.Combine(outputDirectory, $"{baseName}.result.txt");

        try
        {
            File.WriteAllText(scriptPath, scriptContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(wrapperPath, BuildWrapperScript(scriptPath, resultPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{wrapperPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new UtilityExecutionResult(false, "Failed to start the elevated process.");
            }

            process.WaitForExit();

            var output = File.Exists(resultPath)
                ? File.ReadAllText(resultPath)
                : $"No output file was produced. Exit code: {process.ExitCode}.";

            var (cleanedOutput, exitCode) = ParseUtilityOutput(output, process.ExitCode);
            var success = exitCode == 0;
            var summary = success
                ? cleanedOutput
                : $"The elevated command failed with exit code {exitCode}.{Environment.NewLine}{cleanedOutput}";

            return new UtilityExecutionResult(success, summary);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new UtilityExecutionResult(false, "The elevation prompt was cancelled.");
        }
        finally
        {
            TryDelete(scriptPath);
            TryDelete(wrapperPath);
            TryDelete(resultPath);
        }
    }

    private static string BuildWrapperScript(string scriptPath, string resultPath)
    {
        var escapedScriptPath = scriptPath.Replace("'", "''", StringComparison.Ordinal);
        var escapedResultPath = resultPath.Replace("'", "''", StringComparison.Ordinal);
        return $$"""
            $ErrorActionPreference = 'Stop'
            $scriptPath = '{{escapedScriptPath}}'
            $resultPath = '{{escapedResultPath}}'
            $capturedExitCode = 0
            try {
              & $scriptPath *>&1 | Out-File -FilePath $resultPath -Encoding utf8
              if ($LASTEXITCODE) {
                $capturedExitCode = $LASTEXITCODE
              }
            }
            catch {
              $_ | Out-File -FilePath $resultPath -Encoding utf8
              $capturedExitCode = 1
            }
            Add-Content -Path $resultPath -Value "__EXITCODE__=$capturedExitCode" -Encoding utf8
            exit $capturedExitCode
            """;
    }

    private static (string Output, int ExitCode) ParseUtilityOutput(string output, int fallbackExitCode)
    {
        var lines = output
            .Split([Environment.NewLine], StringSplitOptions.None)
            .ToList();

        var exitCode = fallbackExitCode;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            const string exitCodePrefix = "__EXITCODE__=";
            if (!lines[i].StartsWith(exitCodePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (int.TryParse(lines[i][exitCodePrefix.Length..], out var parsedExitCode))
            {
                exitCode = parsedExitCode;
            }

            lines.RemoveAt(i);
            break;
        }

        var cleanedOutput = string.Join(Environment.NewLine, lines).Trim();
        return (
            string.IsNullOrWhiteSpace(cleanedOutput)
                ? "The command completed without additional output."
                : cleanedOutput,
            exitCode);
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalidChars.Contains(ch) ? '-' : ch).ToArray());
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

internal static class UtilityScriptBuilder
{
    public static string BuildRestartServicesScript(IReadOnlyCollection<string> serviceNames)
    {
        var quotedNames = string.Join(", ", serviceNames.Select(name => $"'{name.Replace("'", "''", StringComparison.Ordinal)}'"));
        return $$"""
            $ErrorActionPreference = 'Stop'
            $serviceNames = @({{quotedNames}})
            $results = @()
            foreach ($serviceName in $serviceNames) {
              try {
                $service = Get-Service -Name $serviceName -ErrorAction Stop
                if ($service.Status -eq 'Running') {
                  Stop-Service -Name $serviceName -Force -ErrorAction Stop
                  $service.Refresh()
                  $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(20))
                }
                Start-Service -Name $serviceName -ErrorAction Stop
                $service.Refresh()
                $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(20))
                $results += "SUCCESS: $serviceName restarted."
              }
              catch {
                $results += "FAILED: $serviceName - $($_.Exception.Message)"
              }
            }
            $results -join [Environment]::NewLine
            """;
    }

    public static string BuildRecoverDisplayDeviceScript(string instanceId)
    {
        var escapedInstanceId = instanceId.Replace("'", "''", StringComparison.Ordinal);
        return $$"""
            $ErrorActionPreference = 'Stop'
            $instanceId = '{{escapedInstanceId}}'
            Enable-PnpDevice -InstanceId $instanceId -Confirm:$false -ErrorAction Stop | Out-Null
            Start-Sleep -Seconds 3
            pnputil /restart-device "$instanceId"
            $restartExitCode = $LASTEXITCODE
            pnputil /scan-devices
            $scanExitCode = $LASTEXITCODE
            if ($restartExitCode -ne 0 -or $scanExitCode -ne 0) {
              throw "pnputil failed. restart-device=$restartExitCode scan-devices=$scanExitCode"
            }
            "Recovered display device: $instanceId"
            """;
    }

    public static string BuildSwitchDriverScript(string publishedName, string? originalName, string instanceId)
    {
        var escapedPublishedName = publishedName.Replace("'", "''", StringComparison.Ordinal);
        var escapedOriginalName = (originalName ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
        var escapedInstanceId = instanceId.Replace("'", "''", StringComparison.Ordinal);

        return $$"""
            $ErrorActionPreference = 'Stop'
            $publishedName = '{{escapedPublishedName}}'
            $expectedOriginalName = '{{escapedOriginalName}}'
            $instanceId = '{{escapedInstanceId}}'
            $exportRoot = Join-Path $env:TEMP ('GpuCrashDetector-driver-switch-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $exportRoot -Force | Out-Null
            try {
              pnputil /export-driver $publishedName "$exportRoot"
              $exportExitCode = $LASTEXITCODE
              if ($exportExitCode -ne 0) {
                throw "pnputil /export-driver failed with exit code $exportExitCode."
              }

              $infFile = if ([string]::IsNullOrWhiteSpace($expectedOriginalName)) {
                Get-ChildItem -Path $exportRoot -Recurse -Filter *.inf -File | Select-Object -First 1
              }
              else {
                Get-ChildItem -Path $exportRoot -Recurse -Filter $expectedOriginalName -File | Select-Object -First 1
              }

              if (-not $infFile) {
                throw "Could not locate the exported INF file for $publishedName."
              }

              pnputil /add-driver "$($infFile.FullName)" /install
              $installExitCode = $LASTEXITCODE
              pnputil /scan-devices
              $scanExitCode = $LASTEXITCODE
              pnputil /restart-device "$instanceId"
              $restartExitCode = $LASTEXITCODE

              if ($installExitCode -ne 0 -or $scanExitCode -ne 0 -or $restartExitCode -ne 0) {
                throw "pnputil failed. add-driver=$installExitCode scan-devices=$scanExitCode restart-device=$restartExitCode"
              }

              "Attempted AMD driver switch using $publishedName on $instanceId"
            }
            finally {
              Remove-Item -LiteralPath $exportRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
            """;
    }

    public static string BuildDeleteDriverPackageScript(string publishedName)
    {
        var escapedPublishedName = publishedName.Replace("'", "''", StringComparison.Ordinal);
        return $$"""
            $ErrorActionPreference = 'Stop'
            $publishedName = '{{escapedPublishedName}}'
            pnputil /delete-driver "$publishedName" /uninstall
            $deleteExitCode = $LASTEXITCODE
            if ($deleteExitCode -ne 0) {
              throw "pnputil /delete-driver failed with exit code $deleteExitCode."
            }
            pnputil /scan-devices
            $scanExitCode = $LASTEXITCODE
            if ($scanExitCode -ne 0) {
              throw "pnputil /scan-devices failed with exit code $scanExitCode."
            }
            "Deleted AMD driver package: $publishedName"
            """;
    }
}

internal static class DeviceDiscovery
{
    public static IReadOnlyList<DisplayDeviceInfo> GetAmdDisplayDevices()
    {
        const string script = """
            $devices = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
              Where-Object {
                $_.FriendlyName -match 'AMD|Radeon|RX'
              } |
              Select-Object FriendlyName, InstanceId, Present
            $devices | ConvertTo-Json -Compress
            """;

        var output = Program.RunPowerShell(script);
        var json = PowerShellJson.ExtractJson(output);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            if (json.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                return JsonSerializer.Deserialize<List<DisplayDeviceInfo>>(json) ?? [];
            }

            var single = JsonSerializer.Deserialize<DisplayDeviceInfo>(json);
            return single is null ? [] : [single];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

internal static class ServiceDiscovery
{
    public static IReadOnlyList<ServiceInfo> GetAmdServices()
    {
        const string script = """
            $services = Get-Service -ErrorAction SilentlyContinue |
              Where-Object {
                $_.Name -like 'AMD*' -or $_.DisplayName -like 'AMD*'
              } |
              Sort-Object DisplayName |
              Select-Object @{ Name = 'ServiceName'; Expression = { $_.Name } }, DisplayName
            $services | ConvertTo-Json -Compress
            """;

        var output = Program.RunPowerShell(script);
        var json = PowerShellJson.ExtractJson(output);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            if (json.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                return JsonSerializer.Deserialize<List<ServiceInfo>>(json) ?? [];
            }

            var single = JsonSerializer.Deserialize<ServiceInfo>(json);
            return single is null ? [] : [single];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

internal static class DriverDiscovery
{
    public static AmdDriverInventory GetAmdDriverInventory()
    {
        var devices = DeviceDiscovery.GetAmdDisplayDevices();
        var targetDevice = SelectTargetDevice(devices);
        var activeDrivers = GetAmdSignedDisplayDrivers();
        var targetDriverMatches = targetDevice is null
            ? []
            : activeDrivers
                .Where(driver => string.Equals(driver.DeviceId, targetDevice.InstanceId, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        var activeDriver = targetDriverMatches.Length switch
        {
            1 => targetDriverMatches[0],
            _ when targetDevice is null && activeDrivers.Count == 1 => activeDrivers[0],
            _ => null
        };

        var packages = GetAmdDriverPackages();
        var matchedPackages = activeDriver is null
            ? []
            : packages
                .Where(package =>
                    string.Equals(package.PublishedName, activeDriver.InfName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(package.OriginalName, activeDriver.InfName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        var activeCorrelationAmbiguous =
            targetDevice is not null &&
            (targetDriverMatches.Length != 1 || matchedPackages.Length != 1);

        var packageInfos = packages
            .Select(package => package with
            {
                IsActive = matchedPackages.Any(match => string.Equals(match.PublishedName, package.PublishedName, StringComparison.OrdinalIgnoreCase)),
                CouldBeActive = activeCorrelationAmbiguous
            })
            .ToArray();

        return new AmdDriverInventory(
            devices,
            targetDevice,
            activeDrivers,
            activeDriver,
            packageInfos,
            !activeCorrelationAmbiguous && matchedPackages.Length == 1,
            activeCorrelationAmbiguous);
    }

    private static DisplayDeviceInfo? SelectTargetDevice(IReadOnlyList<DisplayDeviceInfo> devices)
    {
        return devices.Count switch
        {
            0 => null,
            1 => devices[0],
            _ => devices.FirstOrDefault(device => device.Present) ?? devices[0]
        };
    }

    private static IReadOnlyList<ActiveDisplayDriverInfo> GetAmdSignedDisplayDrivers()
    {
        const string script = """
            $drivers = Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue |
              Where-Object {
                $_.DeviceClass -eq 'DISPLAY' -and (
                  $_.Manufacturer -match 'AMD|Advanced Micro Devices' -or
                  $_.DriverProviderName -match 'AMD|Advanced Micro Devices' -or
                  $_.DeviceName -match 'AMD|Radeon|RX'
                )
              } |
              Select-Object DeviceName, DeviceID, Manufacturer, DriverProviderName, InfName, DriverVersion, DriverDate, FriendlyName
            $drivers | ConvertTo-Json -Compress
            """;

        var output = Program.RunPowerShell(script);
        var json = PowerShellJson.ExtractJson(output);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            if (json.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                return JsonSerializer.Deserialize<List<ActiveDisplayDriverInfo>>(json) ?? [];
            }

            var single = JsonSerializer.Deserialize<ActiveDisplayDriverInfo>(json);
            return single is null ? [] : [single];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<DriverPackageInfo> GetAmdDriverPackages()
    {
        var output = Program.RunPowerShell("pnputil /enum-drivers /class Display /format csv");
        var csv = ExtractCommandOutput(output);
        if (string.IsNullOrWhiteSpace(csv))
        {
            return [];
        }

        var lines = csv
            .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

        if (lines.Length < 2)
        {
            return [];
        }

        var packages = new List<DriverPackageInfo>();
        foreach (var line in lines.Skip(1))
        {
            var fields = CsvUtility.ParseLine(line);
            if (fields.Count < 10)
            {
                continue;
            }

            var providerName = fields[2];
            var className = fields[3];
            if (!IsAmdProvider(providerName) || !string.Equals(className, "Display", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (driverDate, driverVersion, driverDateSortKey) = ParseDriverVersionField(fields[7]);
            packages.Add(new DriverPackageInfo(
                PublishedName: fields[0],
                OriginalName: NullIfWhiteSpace(fields[1]),
                ProviderName: NullIfWhiteSpace(providerName),
                ClassName: NullIfWhiteSpace(className),
                ClassGuid: NullIfWhiteSpace(fields[4]),
                DriverDate: driverDate,
                DriverVersion: driverVersion,
                SignerName: NullIfWhiteSpace(fields[8]),
                DriverDateSortKey: driverDateSortKey,
                DriverVersionSortKey: driverVersion ?? string.Empty,
                IsActive: false,
                CouldBeActive: false));
        }

        return packages;
    }

    private static bool IsAmdProvider(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               (value.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase));
    }

    private static (string? DriverDate, string? DriverVersion, DateTime DriverDateSortKey) ParseDriverVersionField(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null, DateTime.MinValue);
        }

        var trimmed = value.Trim();
        var lastSpaceIndex = trimmed.LastIndexOf(' ');
        if (lastSpaceIndex <= 0 || lastSpaceIndex == trimmed.Length - 1)
        {
            return (trimmed, null, DateTime.MinValue);
        }

        var driverDate = trimmed[..lastSpaceIndex].Trim();
        var driverVersion = trimmed[(lastSpaceIndex + 1)..].Trim();
        var dateSortKey = DateTime.TryParse(driverDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate) ||
                          DateTime.TryParse(driverDate, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsedDate)
            ? parsedDate
            : DateTime.MinValue;

        return (driverDate, driverVersion, dateSortKey);
    }

    private static string ExtractCommandOutput(string output)
    {
        return string.Join(
            Environment.NewLine,
            output
                .Split([Environment.NewLine], StringSplitOptions.None)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Where(line => !line.StartsWith("Command:", StringComparison.Ordinal))
                .Where(line => !line.StartsWith("ExitCode:", StringComparison.Ordinal))
                .Where(line => !string.Equals(line, "stderr:", StringComparison.Ordinal)));
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

internal static class DriverInventoryReportWriter
{
    public static string WriteReport(string outputDirectory, AmdDriverInventory inventory)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"amd-driver-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, BuildReport(inventory), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    public static string BuildListSummary(AmdDriverInventory inventory, string reportPath)
    {
        var targetSummary = inventory.TargetDevice is null
            ? "No AMD display device was found."
            : $"Target device: {inventory.TargetDevice.FriendlyName ?? inventory.TargetDevice.InstanceId}.";
        var activeSummary = inventory.ActiveDriver is null
            ? "Active driver package could not be determined."
            : $"Active INF: {inventory.ActiveDriver.InfName ?? "unknown"}.";

        return $"{targetSummary}{Environment.NewLine}{activeSummary}{Environment.NewLine}Found {inventory.Packages.Count} AMD driver package(s).{Environment.NewLine}Report: {reportPath}";
    }

    public static string BuildSwitchSummary(DriverPackageInfo selectedPackage, AmdDriverInventory inventory, string reportPath, UtilityExecutionResult result)
    {
        var activePackage = inventory.Packages.FirstOrDefault(package => package.IsActive);
        var activeSummary = activePackage is null
            ? "The active package after the switch could not be confirmed."
            : $"Active package now appears to be {activePackage.PublishedName} ({activePackage.DisplayVersionAndDate}).";

        return $"{result.Summary}{Environment.NewLine}{activeSummary}{Environment.NewLine}Report: {reportPath}";
    }

    public static string BuildDeleteSummary(DriverPackageInfo selectedPackage, AmdDriverInventory inventory, string reportPath, UtilityExecutionResult result)
    {
        var remainingPackages = inventory.Packages.Count;
        return $"{result.Summary}{Environment.NewLine}Remaining AMD packages: {remainingPackages}.{Environment.NewLine}Report: {reportPath}";
    }

    private static string BuildReport(AmdDriverInventory inventory)
    {
        var builder = new StringBuilder();
        builder.AppendLine("AMD Driver Inventory");
        builder.AppendLine(new string('=', 80));
        builder.AppendLine($"Captured: {DateTimeOffset.Now:O}");
        builder.AppendLine();
        builder.AppendLine("Target AMD Display Device");
        builder.AppendLine(new string('-', 80));

        if (inventory.TargetDevice is null)
        {
            builder.AppendLine("No AMD display device was found.");
        }
        else
        {
            builder.AppendLine($"Friendly Name: {inventory.TargetDevice.FriendlyName ?? "unknown"}");
            builder.AppendLine($"Instance ID: {inventory.TargetDevice.InstanceId}");
            builder.AppendLine($"Present: {inventory.TargetDevice.Present}");
        }

        builder.AppendLine();
        builder.AppendLine("Active Signed Driver");
        builder.AppendLine(new string('-', 80));

        if (inventory.ActiveDriver is null)
        {
            builder.AppendLine("Active AMD signed driver could not be determined.");
        }
        else
        {
            builder.AppendLine($"Device Name: {inventory.ActiveDriver.DeviceName ?? "unknown"}");
            builder.AppendLine($"Device ID: {inventory.ActiveDriver.DeviceId ?? "unknown"}");
            builder.AppendLine($"Provider: {inventory.ActiveDriver.DriverProviderName ?? inventory.ActiveDriver.Manufacturer ?? "unknown"}");
            builder.AppendLine($"INF: {inventory.ActiveDriver.InfName ?? "unknown"}");
            builder.AppendLine($"Version: {inventory.ActiveDriver.DriverVersion ?? "unknown"}");
            builder.AppendLine($"Date: {inventory.ActiveDriver.DriverDate ?? "unknown"}");
        }

        builder.AppendLine();
        builder.AppendLine("Driver Store Packages");
        builder.AppendLine(new string('-', 80));

        if (inventory.Packages.Count == 0)
        {
            builder.AppendLine("No AMD display driver packages were found.");
        }
        else
        {
            foreach (var package in inventory.Packages.OrderByDescending(package => package.DriverDateSortKey).ThenBy(package => package.PublishedName, StringComparer.OrdinalIgnoreCase))
            {
                var marker = package.IsActive ? "[ACTIVE]" : package.CouldBeActive ? "[POSSIBLY ACTIVE]" : "[AVAILABLE]";
                builder.AppendLine($"{marker} {package.PublishedName}");
                builder.AppendLine($"  Original INF: {package.OriginalName ?? "unknown"}");
                builder.AppendLine($"  Provider: {package.ProviderName ?? "unknown"}");
                builder.AppendLine($"  Class: {package.ClassName ?? "unknown"}");
                builder.AppendLine($"  Class GUID: {package.ClassGuid ?? "unknown"}");
                builder.AppendLine($"  Version: {package.DriverVersion ?? "unknown"}");
                builder.AppendLine($"  Date: {package.DriverDate ?? "unknown"}");
                builder.AppendLine($"  Signer: {package.SignerName ?? "unknown"}");
            }
        }

        if (inventory.ActivePackageCorrelationAmbiguous)
        {
            builder.AppendLine();
            builder.AppendLine("Warning");
            builder.AppendLine(new string('-', 80));
            builder.AppendLine("The app could not confidently correlate the active AMD display device to exactly one driver-store package. Destructive driver actions are blocked.");
        }

        return builder.ToString().TrimEnd();
    }
}

internal static class DriverPackageSelectionDialog
{
    public static DriverPackageInfo? Select(string title, string prompt, IReadOnlyList<DriverPackageInfo> packages)
    {
        using var form = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(720, 360)
        };

        var promptLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(12, 12, 12, 0),
            Text = prompt
        };

        var listBox = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            Font = SystemFonts.MessageBoxFont,
            DisplayMember = nameof(DriverPackageInfo.SelectionLabel)
        };

        foreach (var package in packages)
        {
            listBox.Items.Add(package);
        }

        if (listBox.Items.Count > 0)
        {
            listBox.SelectedIndex = 0;
        }

        listBox.DoubleClick += (_, _) =>
        {
            if (listBox.SelectedItem is not null)
            {
                form.DialogResult = DialogResult.OK;
                form.Close();
            }
        };

        var buttonsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12),
            Height = 58
        };

        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            AutoSize = true
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true
        };

        buttonsPanel.Controls.Add(okButton);
        buttonsPanel.Controls.Add(cancelButton);

        form.AcceptButton = okButton;
        form.CancelButton = cancelButton;
        form.Controls.Add(listBox);
        form.Controls.Add(buttonsPanel);
        form.Controls.Add(promptLabel);

        return form.ShowDialog() == DialogResult.OK
            ? listBox.SelectedItem as DriverPackageInfo
            : null;
    }
}

internal static class CsvUtility
{
    public static IReadOnlyList<string> ParseLine(string line)
    {
        var values = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    builder.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                values.Add(builder.ToString());
                builder.Clear();
                continue;
            }

            builder.Append(ch);
        }

        values.Add(builder.ToString());
        return values;
    }
}

internal sealed class DriverPackageVersionComparer : IComparer<string>
{
    public static DriverPackageVersionComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (Version.TryParse(x, out var left) && Version.TryParse(y, out var right))
        {
            return left.CompareTo(right);
        }

        return StringComparer.OrdinalIgnoreCase.Compare(x, y);
    }
}

internal static class PowerShellJson
{
    public static string ExtractJson(string output)
    {
        var lines = output
            .Split([Environment.NewLine], StringSplitOptions.None)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.StartsWith("Command:", StringComparison.Ordinal))
            .Where(line => !line.StartsWith("ExitCode:", StringComparison.Ordinal))
            .Where(line => !string.Equals(line, "stderr:", StringComparison.Ordinal))
            .ToArray();

        var combined = string.Join(Environment.NewLine, lines).Trim();
        if (string.IsNullOrWhiteSpace(combined))
        {
            return string.Empty;
        }

        var objectIndex = combined.IndexOf('{');
        var arrayIndex = combined.IndexOf('[');
        var startIndex = objectIndex switch
        {
            -1 => arrayIndex,
            _ when arrayIndex == -1 => objectIndex,
            _ => Math.Min(objectIndex, arrayIndex)
        };

        return startIndex >= 0 ? combined[startIndex..].Trim() : string.Empty;
    }
}

internal sealed record DisplayDeviceInfo(string? FriendlyName, string InstanceId, bool Present);
internal sealed record ServiceInfo(string ServiceName, string DisplayName);
internal sealed record UtilityExecutionResult(bool Success, string Summary);
internal sealed record ActiveDisplayDriverInfo(
    string? DeviceName,
    string? DeviceId,
    string? Manufacturer,
    string? DriverProviderName,
    string? InfName,
    string? DriverVersion,
    string? DriverDate,
    string? FriendlyName);
internal sealed record DriverPackageInfo(
    string PublishedName,
    string? OriginalName,
    string? ProviderName,
    string? ClassName,
    string? ClassGuid,
    string? DriverDate,
    string? DriverVersion,
    string? SignerName,
    DateTime DriverDateSortKey,
    string DriverVersionSortKey,
    bool IsActive,
    bool CouldBeActive)
{
    public string DisplayVersionAndDate => $"{DriverVersion ?? "unknown"} / {DriverDate ?? "unknown"}";

    public string SelectionLabel
        => $"{PublishedName} | {DriverVersion ?? "unknown"} | {DriverDate ?? "unknown"} | {ProviderName ?? "unknown"}";
}
internal sealed record AmdDriverInventory(
    IReadOnlyList<DisplayDeviceInfo> Devices,
    DisplayDeviceInfo? TargetDevice,
    IReadOnlyList<ActiveDisplayDriverInfo> ActiveDrivers,
    ActiveDisplayDriverInfo? ActiveDriver,
    IReadOnlyList<DriverPackageInfo> Packages,
    bool HasConfirmedActivePackage,
    bool ActivePackageCorrelationAmbiguous);

internal sealed class MonitorStatus
{
    private readonly Lock _lock = new();
    private string _state = "Starting";
    private string? _lastTrigger;
    private DateTimeOffset? _lastSnapshotAt;
    private string? _lastReportPath;
    private string? _lastError;
    private DateTimeOffset? _lastErrorAt;

    public void SetState(string state)
    {
        lock (_lock)
        {
            _state = state;
        }
    }

    public void RecordTrigger(string trigger)
    {
        lock (_lock)
        {
            _lastTrigger = trigger;
            _state = "Collecting snapshot";
        }
    }

    public void RecordSnapshot(string trigger, string reportPath)
    {
        lock (_lock)
        {
            _lastTrigger = trigger;
            _lastSnapshotAt = DateTimeOffset.Now;
            _lastReportPath = reportPath;
            _state = "Monitoring";
        }
    }

    public void RecordError(Exception ex)
    {
        lock (_lock)
        {
            _lastError = ex.Message;
            _lastErrorAt = DateTimeOffset.Now;
            _state = "Error";
        }
    }

    public void RecordMessageError(string message)
    {
        lock (_lock)
        {
            _lastError = message;
            _lastErrorAt = DateTimeOffset.Now;
            _state = "Error";
        }
    }

    public MonitorStatusSnapshot CreateSnapshot()
    {
        lock (_lock)
        {
            var hasRecentSnapshot = _lastSnapshotAt.HasValue && DateTimeOffset.Now - _lastSnapshotAt.Value < TimeSpan.FromMinutes(5);
            var hasRecentError = _lastErrorAt.HasValue && DateTimeOffset.Now - _lastErrorAt.Value < TimeSpan.FromMinutes(10);

            return new MonitorStatusSnapshot(
                _state,
                _lastTrigger,
                _lastSnapshotAt,
                _lastReportPath,
                _lastError,
                hasRecentSnapshot,
                hasRecentError);
        }
    }
}

internal sealed record MonitorStatusSnapshot(
    string State,
    string? LastTrigger,
    DateTimeOffset? LastSnapshotAt,
    string? LastReportPath,
    string? LastError,
    bool HasRecentSnapshot,
    bool HasRecentError);

[SupportedOSPlatform("windows")]
internal sealed class GpuMonitor
{
    private static readonly int[] GpuEventIds = [1, 14, 117, 141, 142, 1000, 1001, 4101];
    private static readonly string[] GpuProviders =
    [
        "Display",
        "nvlddmkm",
        "amdkmdag",
        "amdkmdap",
        "IntelGFX",
        "igfx",
        "Application Error",
        "Windows Error Reporting",
        "Microsoft-Windows-DxgKrnl",
        "Microsoft-Windows-Dwm-Core",
        "Microsoft-Windows-WHEA-Logger"
    ];

    private readonly AppOptions _options;
    private readonly MonitorStatus _status;
    private DateTimeOffset _lastEventCheckUtc;
    private string? _lastArtifactFingerprint;
    private readonly Lock _snapshotLock = new();
    private DateTimeOffset _lastSnapshotAtUtc = DateTimeOffset.MinValue;

    public GpuMonitor(AppOptions options, MonitorStatus status)
    {
        _options = options;
        _status = status;
        _lastEventCheckUtc = DateTimeOffset.UtcNow.AddDays(-options.Days);
    }

    public void Run(CancellationToken cancellationToken)
    {
        _status.SetState("Monitoring");
        AppendMonitorLog($"[{DateTimeOffset.Now:O}] GPU background monitor started.");

        using var systemWatcher = CreateWatcher("System");
        using var applicationWatcher = CreateWatcher("Application");

        systemWatcher.Enabled = true;
        applicationWatcher.Enabled = true;

        if (_options.CaptureBaselineOnStart)
        {
            CaptureSnapshot("startup baseline");
        }

        var nextEventPollUtc = DateTimeOffset.UtcNow.AddSeconds(_options.PollIntervalSeconds);
        var nextArtifactPollUtc = DateTimeOffset.UtcNow.AddSeconds(_options.ArtifactPollIntervalSeconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                string? trigger = null;
                var nowUtc = DateTimeOffset.UtcNow;

                if (nowUtc >= nextEventPollUtc)
                {
                    trigger = DetectEventPollTrigger();
                    nextEventPollUtc = nowUtc.AddSeconds(_options.PollIntervalSeconds);
                }

                if (trigger is null && nowUtc >= nextArtifactPollUtc)
                {
                    trigger = DetectArtifactTrigger();
                    nextArtifactPollUtc = nowUtc.AddSeconds(_options.ArtifactPollIntervalSeconds);
                }

                if (trigger is not null)
                {
                    CaptureSnapshot(trigger);
                }
            }
            catch (Exception ex)
            {
                _status.RecordError(ex);
                AppendMonitorLog($"[{DateTimeOffset.Now:O}] monitor error: {ex}");
            }

            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
        }

        _status.SetState("Stopped");
        AppendMonitorLog($"[{DateTimeOffset.Now:O}] GPU background monitor stopped.");
    }

    public void RequestManualSnapshot(string triggerReason)
    {
        CaptureSnapshot(triggerReason);
    }

    private EventLogWatcher CreateWatcher(string logName)
    {
        var query = new EventLogQuery(logName, PathType.LogName, BuildEventWatcherQuery());
        var watcher = new EventLogWatcher(query);
        watcher.EventRecordWritten += (_, args) =>
        {
            if (args.EventException is not null)
            {
                _status.RecordMessageError(args.EventException.Message);
                AppendMonitorLog($"[{DateTimeOffset.Now:O}] event watcher error ({logName}): {args.EventException}");
                return;
            }

            var record = args.EventRecord;
            if (record is null)
            {
                return;
            }

            var providerName = record.ProviderName ?? "Unknown";
            var eventId = record.Id;
            var timestamp = record.TimeCreated?.ToString("O") ?? "unknown-time";
            CaptureSnapshot($"real-time event: {logName}/{providerName}/{eventId} at {timestamp}");
        };

        return watcher;
    }

    private static string BuildEventWatcherQuery()
    {
        var providerFilter = string.Join(" or ", GpuProviders.Select(provider => $"Provider[@Name='{provider}']"));
        var eventIdFilter = string.Join(" or ", GpuEventIds.Select(id => $"EventID={id}"));
        return $"*[System[({providerFilter}) or ({eventIdFilter})]]";
    }

    private string? DetectEventPollTrigger()
    {
        var newEvents = GetRecentTriggerEvents();
        if (!string.IsNullOrWhiteSpace(newEvents))
        {
            _lastEventCheckUtc = DateTimeOffset.UtcNow;
            AppendMonitorLog($"[{DateTimeOffset.Now:O}] trigger matched fallback event log activity.");
            return "new GPU-related event log entries detected by fallback poll";
        }

        return null;
    }

    private string? DetectArtifactTrigger()
    {
        var artifactFingerprint = GetCrashArtifactFingerprint();
        if (!string.IsNullOrWhiteSpace(artifactFingerprint) &&
            !string.Equals(artifactFingerprint, _lastArtifactFingerprint, StringComparison.Ordinal))
        {
            _lastArtifactFingerprint = artifactFingerprint;
            AppendMonitorLog($"[{DateTimeOffset.Now:O}] trigger matched crash artifacts: {artifactFingerprint}");
            return $"new crash artifact detected ({artifactFingerprint})";
        }

        return null;
    }

    private string GetRecentTriggerEvents()
    {
        var start = _lastEventCheckUtc.LocalDateTime.ToString("O");
        var result = Program.RunPowerShell($@"
$start = [datetimeoffset]::Parse('{start}')
$providers = @(
  'Display',
  'nvlddmkm',
  'amdkmdag',
  'amdkmdap',
  'IntelGFX',
  'igfx',
  'Application Error',
  'Windows Error Reporting',
  'Microsoft-Windows-DxgKrnl',
  'Microsoft-Windows-Dwm-Core',
  'Microsoft-Windows-WHEA-Logger'
)
$keywords = 'display driver|stopped responding|recovered|TDR|timeout detection|LiveKernelEvent|graphics|GPU'
Get-WinEvent -FilterHashtable @{{ LogName = @('System', 'Application'); StartTime = $start.DateTime }} -ErrorAction SilentlyContinue |
  Where-Object {{
    $providers -contains $_.ProviderName -or
    $_.Id -in 4101, 14, 117, 141, 142, 1, 1000, 1001 -or
    ($_.Message -and $_.Message -match $keywords)
  }} |
  Select-Object -First 10 TimeCreated, Id, ProviderName |
  Format-Table -AutoSize
");

        return result.Contains("ExitCode: 0", StringComparison.Ordinal) &&
               result.Contains("ProviderName", StringComparison.Ordinal)
            ? result
            : string.Empty;
    }

    private string GetCrashArtifactFingerprint()
    {
        var result = Program.RunPowerShell("""
            $paths = @(
              "$env:SystemRoot\LiveKernelReports",
              "$env:SystemRoot\Minidump",
              "$env:ProgramData\Microsoft\Windows\WER\ReportArchive",
              "$env:ProgramData\Microsoft\Windows\WER\ReportQueue"
            )
            $latest = foreach ($path in $paths) {
              if (Test-Path $path) {
                Get-ChildItem -Path $path -Recurse -File -ErrorAction SilentlyContinue |
                  Sort-Object LastWriteTime -Descending |
                  Select-Object -First 1 FullName, LastWriteTime
              }
            } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($latest) {
              "{0}|{1:O}" -f $latest.FullName, $latest.LastWriteTime
            }
            """);

        return ExtractLastNonEmptyLine(result);
    }

    private void CaptureSnapshot(string triggerReason)
    {
        lock (_snapshotLock)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            if (nowUtc - _lastSnapshotAtUtc < TimeSpan.FromSeconds(15))
            {
                AppendMonitorLog($"[{DateTimeOffset.Now:O}] snapshot skipped due to debounce: {triggerReason}");
                return;
            }

            _lastSnapshotAtUtc = nowUtc;
        }

        _status.RecordTrigger(triggerReason);
        var report = Program.CollectDiagnosticReport(_options, triggerReason);
        var reportPath = Path.Combine(_options.OutputDirectory, $"gpu-diagnostic-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(reportPath, report.Render(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        _status.RecordSnapshot(triggerReason, reportPath);
        AppendMonitorLog(report.RenderSummary(reportPath, report.DxDiagPath ?? "not generated"));
    }

    private void AppendMonitorLog(string message)
    {
        var monitorLogPath = Path.Combine(_options.OutputDirectory, "monitor.log");
        File.AppendAllText(monitorLogPath, message + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string ExtractLastNonEmptyLine(string value)
    {
        return value
            .Split([Environment.NewLine], StringSplitOptions.None)
            .Select(line => line.Trim())
            .LastOrDefault(line => !string.IsNullOrWhiteSpace(line) &&
                                   !line.StartsWith("Command:", StringComparison.Ordinal) &&
                                   !line.StartsWith("ExitCode:", StringComparison.Ordinal) &&
                                   !string.Equals(line, "stderr:", StringComparison.Ordinal)) ?? string.Empty;
    }
}
