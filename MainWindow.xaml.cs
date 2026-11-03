using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Security.Principal;
using Deviso.Models;
using Deviso.Services;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using ToolTipIcon = System.Windows.Forms.ToolTipIcon;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using ToolStripItem = System.Windows.Forms.ToolStripItem;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace Deviso;

public partial class MainWindow : Window
{
    private readonly DiskService _diskService = new(new PowerShellRunner());
    private readonly BenchmarkService _benchmarkService = new();
    private readonly FileSystemCheckService _checkService = new();
    private readonly SmartTestService _smartTestService = new();
    private readonly HistoryService _historyService = new();
    private readonly AlertSettingsService _alertSettingsService = new();
    private readonly StartMenuShortcutService _startMenuShortcutService = new();
    private readonly ICollectionView _driveView;
    private readonly RemoteMonitoringService _remoteMonitoringService;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly System.Drawing.Icon _appIcon;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _notifyContextMenu;
    private HashSet<string> _activeAlerts = [];
    private int _chartRenderVersion;
    private bool _isBusy;

    public ObservableCollection<PhysicalDisk> Drives { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not locate the Deviso executable.");
        _appIcon = System.Drawing.Icon.ExtractAssociatedIcon(executable)
            ?? throw new InvalidOperationException("Could not load the Deviso application icon.");
        _notifyContextMenu = CreateNotifyContextMenu();
        _notifyIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "Deviso drive health",
            ContextMenuStrip = _notifyContextMenu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => DispatchTrayCommand(TaskbarCommand.Open);
        _remoteMonitoringService = new RemoteMonitoringService(_diskService);
        DataContext = this;
        _driveView = CollectionViewSource.GetDefaultView(Drives);
        _driveView.Filter = IsDriveVisible;
        _refreshTimer.Tick += async (_, _) => await RefreshDrivesAsync();
        Loaded += async (_, _) =>
        {
            RestartAdminButton.IsEnabled = !IsRunningAsAdministrator();
            UpdateStartMenuShortcutStatus();
            try
            {
                await _alertSettingsService.LoadAsync();
                TemperatureThresholdBox.Text = _alertSettingsService.TemperatureThreshold.ToString();
            }
            catch (Exception exception)
            {
                AppendActivity($"[{DateTime.Now:G}] ERROR loading alert settings: {exception.Message}");
                MessageBox.Show(this, exception.Message, "Deviso — settings could not be loaded",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            _refreshTimer.Start();
            await RefreshDrivesAsync();
        };
        Closed += async (_, _) =>
        {
            _refreshTimer.Stop();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyContextMenu.Dispose();
            _appIcon.Dispose();
            await _remoteMonitoringService.DisposeAsync();
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshDrivesAsync();

    private ContextMenuStrip CreateNotifyContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(CreateTrayMenuItem("Open Deviso", TaskbarCommand.Open));
        menu.Items.Add(CreateTrayMenuItem("Refresh drives", TaskbarCommand.Refresh));
        menu.Items.Add(CreateTrayMenuItem("Reports and history", TaskbarCommand.Reports));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(CreateTrayMenuItem("Exit Deviso", TaskbarCommand.Exit));
        return menu;
    }

    private ToolStripItem CreateTrayMenuItem(string title, string command)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(title);
        item.Click += (_, _) => DispatchTrayCommand(command);
        return item;
    }

    private void DispatchTrayCommand(string command)
    {
        _ = Dispatcher.BeginInvoke(() => HandleTaskbarCommand(command));
    }

    public void HandleTaskbarCommand(string command)
    {
        switch (command)
        {
            case TaskbarCommand.Open:
                MainTabs.SelectedIndex = 0;
                RestoreAndActivate();
                break;
            case TaskbarCommand.Refresh:
                MainTabs.SelectedIndex = 0;
                RestoreAndActivate();
                _ = RefreshDrivesAsync();
                break;
            case TaskbarCommand.Reports:
                MainTabs.SelectedIndex = MainTabs.Items.Count - 1;
                RestoreAndActivate();
                break;
            case TaskbarCommand.Exit:
                Close();
                break;
        }
    }

    private void RestoreAndActivate()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
    }

    private async Task RefreshDrivesAsync()
    {
        if (_isBusy)
        {
            return;
        }

        SetBusy(true, "Reading Windows storage health data…");
        try
        {
            var selectedDeviceId = (DriveGrid.SelectedItem as PhysicalDisk)?.DeviceId;
            var disks = await _diskService.GetDisksAsync();
            Drives.Clear();
            foreach (var disk in disks)
            {
                Drives.Add(disk);
            }

            DriveGrid.SelectedItem = disks.FirstOrDefault(disk =>
                string.Equals(disk.DeviceId, selectedDeviceId, StringComparison.OrdinalIgnoreCase))
                ?? disks.FirstOrDefault();
            UpdateDriveEmptyState();
            DriveCountText.Text = disks.Count.ToString();
            HealthyCountText.Text = disks.Count(d => string.Equals(
                d.Health, "Healthy", StringComparison.OrdinalIgnoreCase)).ToString();
            var warmestDisk = disks.Where(disk => disk.Temperature.HasValue)
                .MaxBy(disk => disk.Temperature);
            TemperatureSummaryText.Text = warmestDisk?.TemperatureDisplay ?? "Not reported";

            await _historyService.AppendAsync(disks);
            var warningDisks = disks
                .Where(d => d.Temperature >= _alertSettingsService.TemperatureThreshold ||
                            !string.Equals(d.Health, "Healthy", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var unsupported = disks
                .Where(d => !string.IsNullOrWhiteSpace(d.ReliabilityError))
                .Select(d => $"{d.DisplayName}: reliability counters unavailable ({d.ReliabilityError})")
                .ToArray();

            var lines = new List<string>
            {
                $"[{DateTime.Now:G}] Found {disks.Count} physical disk(s)."
            };
            lines.AddRange(warningDisks.Select(disk =>
                $"ATTENTION: {disk.DisplayName}: health={disk.Health}, temperature={disk.TemperatureDisplay}"));
            lines.AddRange(unsupported.Select(message => $"INFO: {message}"));
            AppendActivity(string.Join(Environment.NewLine, lines));
            NotifyNewWarnings(warningDisks);
            SetBusy(false, $"Last checked {DateTime.Now:T}. History: {_historyService.HistoryPath}");
            RestartAdminButton.IsEnabled = !IsRunningAsAdministrator();
            UpdateSelectedDriveDetails();
        }
        catch (Exception exception)
        {
            SetBusy(false, "Drive refresh failed.");
            AppendActivity($"[{DateTime.Now:G}] ERROR: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Deviso — refresh failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Benchmark_Click(object sender, RoutedEventArgs e)
    {
        var disk = DriveGrid.SelectedItem as PhysicalDisk;
        if (disk is null)
        {
            ShowSelectDiskMessage();
            return;
        }

        SetBusy(true, $"Benchmarking {disk.DisplayName}…");
        try
        {
            var result = await _benchmarkService.RunAsync(disk);
            AppendActivity($"[{DateTime.Now:G}] {disk.DisplayName}: {result}");
            SetBusy(false, "Benchmark complete.");
        }
        catch (Exception exception)
        {
            SetBusy(false, "Benchmark failed.");
            AppendActivity($"[{DateTime.Now:G}] ERROR: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Deviso — benchmark failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var disk = DriveGrid.SelectedItem as PhysicalDisk;
        if (disk is null)
        {
            ShowSelectDiskMessage();
            return;
        }

        var answer = MessageBox.Show(this,
            $"Run a filesystem check for {disk.VolumeRoot} without requesting repairs? This does not run a physical surface test.",
            "Confirm filesystem check", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        SetBusy(true, $"Checking {disk.VolumeRoot}…");
        try
        {
            var result = await _checkService.RunAsync(disk);
            AppendActivity($"[{DateTime.Now:G}] {disk.DisplayName} filesystem check:{Environment.NewLine}{result}");
            SetBusy(false, "Filesystem check complete. No repair option was requested.");
        }
        catch (Exception exception)
        {
            SetBusy(false, "Filesystem check failed.");
            AppendActivity($"[{DateTime.Now:G}] ERROR: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Deviso — check failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SmartTest_Click(object sender, RoutedEventArgs e)
    {
        var disk = DriveGrid.SelectedItem as PhysicalDisk;
        if (disk is null)
        {
            ShowSelectDiskMessage();
            return;
        }

        var extended = SmartTestMode.SelectedIndex == 1;
        var testName = extended ? "extended" : "short";
        var answer = MessageBox.Show(this,
            $"Start a {testName} SMART self-test on {disk.DisplayName}? This asks the drive to run its own diagnostic. It may take time and is available only if the drive and controller support it. Deviso will not repair data.",
            "Confirm SMART self-test", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        SetBusy(true, $"Starting {testName} SMART self-test…");
        try
        {
            var result = await _smartTestService.StartAsync(disk, extended);
            AppendActivity($"[{DateTime.Now:G}] {disk.DisplayName} {testName} SMART self-test:{Environment.NewLine}{result}");
            SetBusy(false, "SMART self-test started. Check the drive with smartmontools for progress/results.");
        }
        catch (Exception exception)
        {
            SetBusy(false, "SMART self-test could not be started.");
            AppendActivity($"[{DateTime.Now:G}] ERROR: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Deviso — SMART self-test failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RestartAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (IsRunningAsAdministrator())
        {
            MessageBox.Show(this, "Deviso is already running with administrator access.",
                "Deviso", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            "Windows may expose additional drive temperature, power-on, and wear counters to an administrator. This still depends on your drive and controller. Restart Deviso with elevated access?",
            "Unlock drive telemetry", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not locate the Deviso executable.");
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
            System.Windows.Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            AppendActivity($"[{DateTime.Now:G}] Administrator access was cancelled.");
            SetBusy(false, "Administrator access was cancelled.");
        }
        catch (Exception exception)
        {
            AppendActivity($"[{DateTime.Now:G}] ERROR requesting administrator access: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Deviso — could not elevate",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StartRemote_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Starting private LAN monitor…");
        try
        {
            var info = await _remoteMonitoringService.StartAsync();
            RemoteAddressesText.Text = string.Join(Environment.NewLine,
                info.Addresses.Select(address => $"https://{address}:{info.Port}/api/health"));
            RemoteTokenText.Text = info.PairingToken;
            RemoteFingerprintText.Text = info.CertificateSha256;
            StartRemoteButton.IsEnabled = false;
            StopRemoteButton.IsEnabled = true;
            AppendActivity($"[{DateTime.Now:G}] Private LAN monitoring started on {string.Join(", ", info.Addresses)}.");
            SetBusy(false, "LAN monitor is running. Share pairing details only with a trusted peer.");
        }
        catch (Exception exception)
        {
            SetBusy(false, "Could not start LAN monitoring.");
            AppendActivity($"[{DateTime.Now:G}] ERROR: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Deviso — remote monitor failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StopRemote_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _remoteMonitoringService.StopAsync();
            RemoteAddressesText.Text = "Not running";
            RemoteTokenText.Clear();
            RemoteFingerprintText.Clear();
            StartRemoteButton.IsEnabled = true;
            StopRemoteButton.IsEnabled = false;
            SetBusy(false, "LAN monitor stopped.");
            AppendActivity($"[{DateTime.Now:G}] Private LAN monitoring stopped.");
        }
        catch (Exception exception)
        {
            AppendActivity($"[{DateTime.Now:G}] ERROR stopping LAN monitor: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Deviso — could not stop remote monitor",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ConnectPeer_Click(object sender, RoutedEventArgs e)
    {
        ConnectPeerButton.IsEnabled = false;
        PeerStatusText.Text = "Connecting and validating the pinned certificate…";
        try
        {
            var disks = await _remoteMonitoringService.ReadPeerAsync(
                PeerAddressBox.Text.Trim(),
                PeerTokenBox.Password.Trim(),
                PeerFingerprintBox.Text.Trim());
            RemoteDriveGrid.ItemsSource = disks;
            RemoteDriveGrid.Visibility = Visibility.Visible;
            PeerStatusText.Text = $"Connected securely. Received {disks.Count} drive(s). The connection is read-only.";
        }
        catch (Exception exception)
        {
            PeerStatusText.Text = $"Connection failed: {exception.Message}";
            MessageBox.Show(this, exception.Message, "Deviso — peer connection failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ConnectPeerButton.IsEnabled = true;
        }
    }

    private async void SaveAlertSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TemperatureThresholdBox.Text, out var threshold) ||
            threshold is < 30 or > 100)
        {
            MessageBox.Show(this, "Enter a temperature threshold from 30 to 100 °C.",
                "Deviso — invalid threshold", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await _alertSettingsService.SaveTemperatureThresholdAsync(threshold);
            SetBusy(false, $"Temperature warning threshold set to {threshold} °C.");
            AppendActivity($"[{DateTime.Now:G}] Temperature warning threshold set to {threshold} °C.");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Deviso — settings could not be saved",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (Drives.Count == 0)
        {
            MessageBox.Show(this, "Refresh the drive list before exporting a report.",
                "Deviso", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Deviso drive-health report",
            FileName = $"Deviso-health-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            DefaultExt = ".csv",
            Filter = "CSV report (*.csv)|*.csv"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var report = new StringBuilder();
            report.AppendLine("Deviso drive-health report");
            report.AppendLine($"Generated (local):,{CsvField(DateTimeOffset.Now.ToString("O"))}");
            report.AppendLine("Source,Windows Storage reliability counters (availability depends on device/controller)");
            report.AppendLine("Device ID,Name,Bus,Media type,Capacity (bytes),Windows health,Operational status,Volume,Temperature (C),Power-on hours,Wear (% used),SMART life remaining (%),SMART life indicator source,Data source,Notes");
            foreach (var disk in Drives)
            {
                report.AppendLine(string.Join(",", new[]
                {
                    CsvField(disk.DeviceId),
                    CsvField(disk.DisplayName),
                    CsvField(disk.BusType),
                    CsvField(disk.MediaType),
                    CsvField(disk.Size.ToString()),
                    CsvField(disk.Health),
                    CsvField(disk.OperationalStatus),
                    CsvField(disk.VolumeRoot ?? ""),
                    CsvField(disk.Temperature?.ToString() ?? ""),
                    CsvField(disk.PowerOnHours?.ToString() ?? ""),
                    CsvField(disk.Wear?.ToString() ?? ""),
                    CsvField(disk.LifeRemainingPercentage?.ToString() ?? ""),
                    CsvField(disk.LifeRemainingSource ?? ""),
                    CsvField(disk.DataSourceDisplay),
                    CsvField(disk.ReliabilityError ?? "")
                }));
            }

            await File.WriteAllTextAsync(dialog.FileName, report.ToString(), Encoding.UTF8);
            AppendActivity($"[{DateTime.Now:G}] Drive-health CSV exported to {dialog.FileName}.");
            SetBusy(false, $"Report exported: {dialog.FileName}");
        }
        catch (Exception exception)
        {
            AppendActivity($"[{DateTime.Now:G}] ERROR exporting report: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Deviso — report export failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_historyService.HistoryPath)
                ?? throw new InvalidOperationException("Could not determine the history folder.");
            Directory.CreateDirectory(directory);
            if (!File.Exists(_historyService.HistoryPath))
            {
                File.WriteAllText(_historyService.HistoryPath, "");
            }

            Process.Start(new ProcessStartInfo(_historyService.HistoryPath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Deviso — could not open history",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowSelectDiskMessage() =>
        MessageBox.Show(this, "Select a disk first.", "Deviso", MessageBoxButton.OK, MessageBoxImage.Information);

    private void AddStartMenuShortcut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _startMenuShortcutService.Create();
            UpdateStartMenuShortcutStatus();
            AppendActivity($"[{DateTime.Now:G}] Added Deviso to the current user's Windows Start menu.");
        }
        catch (Exception exception)
        {
            StartMenuShortcutStatusText.Text = $"Could not add shortcut: {exception.Message}";
            AppendActivity($"[{DateTime.Now:G}] ERROR creating Start menu shortcut: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Deviso — Start menu shortcut failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveStartMenuShortcut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _startMenuShortcutService.Remove();
            UpdateStartMenuShortcutStatus();
            AppendActivity($"[{DateTime.Now:G}] Removed Deviso from the current user's Windows Start menu.");
        }
        catch (Exception exception)
        {
            StartMenuShortcutStatusText.Text = $"Could not remove shortcut: {exception.Message}";
            AppendActivity($"[{DateTime.Now:G}] ERROR removing Start menu shortcut: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Deviso — Start menu shortcut failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateStartMenuShortcutStatus()
    {
        if (!_startMenuShortcutService.IsAvailable)
        {
            StartMenuShortcutStatusText.Text = "Windows did not provide a Start menu folder; shortcut controls are unavailable.";
            AddStartMenuShortcutButton.IsEnabled = false;
            RemoveStartMenuShortcutButton.IsEnabled = false;
            return;
        }

        var exists = _startMenuShortcutService.IsInstalled;
        StartMenuShortcutStatusText.Text = exists
            ? "Deviso is in Start. If an old taskbar pin has no actions, unpin it and pin Deviso from Start again."
            : "Add a shortcut to launch Deviso from the Start menu.";
        AddStartMenuShortcutButton.IsEnabled = !exists;
        RemoveStartMenuShortcutButton.IsEnabled = exists;
    }

    private void DriveGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedDriveDetails();
    }

    private void DriveSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_driveView is null)
        {
            return;
        }

        _driveView.Refresh();
        DriveSearchHint.Visibility = string.IsNullOrEmpty(DriveSearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateDriveEmptyState();
    }

    private bool IsDriveVisible(object item)
    {
        if (item is not PhysicalDisk disk || string.IsNullOrWhiteSpace(DriveSearchBox.Text))
        {
            return item is PhysicalDisk;
        }

        var query = DriveSearchBox.Text.Trim();
        return disk.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               disk.DeviceId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               disk.BusType.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               disk.MediaType.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               disk.Health.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               disk.VolumeDisplay.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateDriveEmptyState()
    {
        var isEmpty = DriveGrid.Items.Count == 0;
        DriveGrid.Height = Math.Clamp(40 + (DriveGrid.Items.Count * 42), 128, 300);
        DriveEmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        DriveEmptyTitle.Text = Drives.Count == 0 ? "No drives found" : "No matching drives";
        DriveEmptyMessage.Text = Drives.Count == 0
            ? "Refresh to check for storage devices."
            : "Try a different search, or clear the search field to see all drives.";
    }

    private void UpdateSelectedDriveDetails()
    {
        var disk = DriveGrid.SelectedItem as PhysicalDisk;
        LifeSummaryText.Text = disk?.LifeRemainingDisplay ?? "Select a drive";
        LifeSummaryCaption.Text = disk?.LifeRemainingPercentage is null
            ? "Reported when supported by the drive"
            : disk.LifeRemainingCaption;
        SelectedDriveNameText.Text = disk is null
            ? "Choose a drive"
            : $"{disk.BusType} · {disk.Capacity} · {disk.VolumeDisplay}";
        SelectedHealthText.Text = disk?.Health ?? "—";
        var healthColor = disk?.Health.ToLowerInvariant() switch
        {
            "healthy" => Color.FromRgb(98, 201, 155),
            "warning" or "unhealthy" or "critical" => Color.FromRgb(240, 110, 102),
            _ => Color.FromRgb(215, 169, 104)
        };
        SelectedHealthText.Foreground = new SolidColorBrush(healthColor);
        SelectedSourceText.Text = disk is null
            ? "Windows drive status"
            : $"{disk.DataSourceDisplay}{(string.IsNullOrWhiteSpace(disk.ReliabilityError) ? "" : Environment.NewLine + disk.ReliabilityError)}";
        SelectedTemperatureText.Text = disk?.TemperatureDisplay ?? "—";
        SelectedPowerOnText.Text = disk?.PowerOnDisplay ?? "—";
        SelectedWearText.Text = disk?.WearDisplay ?? "—";
        SelectedLifeText.Text = disk?.LifeRemainingDisplay ?? "Not reported";
        var lifeColor = disk?.LifeRemainingPercentage switch
        {
            >= 50 => Color.FromRgb(98, 201, 155),
            >= 20 => Color.FromRgb(254, 154, 90),
            not null => Color.FromRgb(240, 110, 102),
            _ => Color.FromRgb(154, 167, 180)
        };
        SelectedLifeText.Foreground = new SolidColorBrush(lifeColor);
        LifeProgress.Foreground = new SolidColorBrush(lifeColor);
        LifeProgress.Visibility = disk?.LifeRemainingPercentage is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        LifeProgress.Value = disk?.LifeRemainingPercentage ?? 0;
        LifeCaptionText.Text = disk is null || disk.LifeRemainingPercentage is null
            ? "Life percentage is shown only when the drive reports a supported SMART wear value."
            : $"{disk.LifeRemainingCaption}; this percentage is not a time-remaining estimate.";

        if (MainTabs.SelectedIndex == 1)
        {
            _ = DrawTemperatureHistoryAsync();
        }
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, MainTabs) && MainTabs.SelectedIndex == 1)
        {
            _ = DrawTemperatureHistoryAsync();
        }
    }

    private void TemperatureChart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (MainTabs.SelectedIndex == 1)
        {
            _ = DrawTemperatureHistoryAsync();
        }
    }

    private async Task DrawTemperatureHistoryAsync()
    {
        var renderVersion = ++_chartRenderVersion;
        var disk = DriveGrid.SelectedItem as PhysicalDisk;
        TemperatureChart.Children.Clear();
        if (disk is null)
        {
            AddChartMessage("Select a drive in Overview to view its recorded readings.");
            return;
        }

        try
        {
            var readings = await _historyService.ReadTemperatureHistoryAsync(disk.DeviceId);
            if (renderVersion != _chartRenderVersion ||
                !string.Equals((DriveGrid.SelectedItem as PhysicalDisk)?.DeviceId,
                    disk.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            TemperatureHistorySubtitle.Text = readings.Count == 0
                ? $"{disk.DisplayName} · readings are added when a temperature is available."
                : $"{disk.DisplayName} · {readings.Count} recorded readings";

            if (readings.Count == 0)
            {
                AddChartMessage("No temperature readings recorded yet.");
                return;
            }

            DrawTemperatureChart(readings);
        }
        catch (Exception exception)
        {
            TemperatureHistorySubtitle.Text = "Could not read the temperature history.";
            AppendActivity($"[{DateTime.Now:G}] ERROR: {exception.Message}");
        }
    }

    private void DrawTemperatureChart(IReadOnlyList<TemperatureReading> readings)
    {
        var width = TemperatureChart.ActualWidth;
        var height = TemperatureChart.ActualHeight;
        if (width < 180 || height < 140)
        {
            return;
        }

        const double left = 52;
        const double right = 18;
        const double top = 18;
        const double bottom = 40;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        var minValue = Math.Max(0, readings.Min(reading => reading.Temperature) - 4);
        var maxValue = readings.Max(reading => reading.Temperature) + 4;
        if (maxValue <= minValue)
        {
            maxValue = minValue + 8;
        }

        var gridBrush = new SolidColorBrush(Color.FromRgb(40, 52, 66));
        var labelBrush = new SolidColorBrush(Color.FromRgb(132, 148, 166));
        for (var index = 0; index <= 4; index++)
        {
            var y = top + plotHeight * index / 4;
            TemperatureChart.Children.Add(new Line
            {
                X1 = left,
                X2 = width - right,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 1
            });
            AddChartLabel($"{maxValue - (maxValue - minValue) * index / 4:0}°",
                0, y - 9, labelBrush);
        }

        var points = new PointCollection();
        for (var index = 0; index < readings.Count; index++)
        {
            var x = readings.Count == 1
                ? left + plotWidth / 2
                : left + plotWidth * index / (readings.Count - 1);
            var y = top + plotHeight * (maxValue - readings[index].Temperature) / (maxValue - minValue);
            points.Add(new Point(x, y));
        }

        TemperatureChart.Children.Add(new Polyline
        {
            Points = points,
            Stroke = new SolidColorBrush(Color.FromRgb(254, 127, 45)),
            StrokeThickness = 2.5
        });

        for (var index = 0; index < points.Count; index++)
        {
            var marker = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(Color.FromRgb(254, 127, 45))
            };
            Canvas.SetLeft(marker, points[index].X - 4);
            Canvas.SetTop(marker, points[index].Y - 4);
            TemperatureChart.Children.Add(marker);
        }

        AddChartLabel(readings[0].Timestamp.LocalDateTime.ToString("MMM d, HH:mm"),
            left, height - bottom + 12, labelBrush);
        if (readings.Count > 1)
        {
            var lastLabel = readings[^1].Timestamp.LocalDateTime.ToString("MMM d, HH:mm");
            AddChartLabel(lastLabel, width - right - 112, height - bottom + 12, labelBrush);
        }
    }

    private void AddChartMessage(string text)
    {
        var message = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(132, 148, 166)),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Width = Math.Max(160, TemperatureChart.ActualWidth - 48)
        };
        Canvas.SetLeft(message, 24);
        Canvas.SetTop(message, Math.Max(12, TemperatureChart.ActualHeight / 2 - 20));
        TemperatureChart.Children.Add(message);
    }

    private void AddChartLabel(
        string text, double left, double top, System.Windows.Media.Brush foreground)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            FontSize = 11
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        TemperatureChart.Children.Add(label);
    }

    private void AppendActivity(string message)
    {
        ActivityText.AppendText(message + Environment.NewLine);
        ActivityText.ScrollToEnd();
    }

    private void SetBusy(bool busy, string status)
    {
        _isBusy = busy;
        StatusText.Text = status;
        StatusText.ToolTip = status;
        StatusIndicator.Fill = new SolidColorBrush(busy
            ? Color.FromRgb(254, 127, 45)
            : status.Contains("fail", StringComparison.OrdinalIgnoreCase)
                ? Color.FromRgb(240, 110, 102)
                : Color.FromRgb(98, 201, 155));
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        DriveGrid.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        BenchmarkButton.IsEnabled = !busy;
        CheckButton.IsEnabled = !busy;
        SmartTestButton.IsEnabled = !busy;
        SaveAlertSettingsButton.IsEnabled = !busy;
    }

    private void NotifyNewWarnings(IEnumerable<PhysicalDisk> warningDisks)
    {
        var alerts = warningDisks.ToArray();
        string AlertKey(PhysicalDisk disk) =>
            $"{disk.DeviceId}:{!string.Equals(disk.Health, "Healthy", StringComparison.OrdinalIgnoreCase)}:" +
            $"{disk.Temperature >= _alertSettingsService.TemperatureThreshold}";
        var currentKeys = alerts.Select(AlertKey).ToHashSet();
        var newAlerts = alerts
            .Where(disk => !_activeAlerts.Contains(AlertKey(disk)))
            .ToArray();
        _activeAlerts = currentKeys;

        if (newAlerts.Length > 0)
        {
            var summary = string.Join("; ", newAlerts.Select(disk =>
                $"{disk.DisplayName}: {disk.Health}, {disk.TemperatureDisplay}"));
            _notifyIcon.ShowBalloonTip(5000, "Deviso drive warning", summary, ToolTipIcon.Warning);
        }
    }

    private static string CsvField(string value) =>
        $"\"{value.Replace("\"", "\"\"")}\"";

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
