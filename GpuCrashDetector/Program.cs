using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

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
