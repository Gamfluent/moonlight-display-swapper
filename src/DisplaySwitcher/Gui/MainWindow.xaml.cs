using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DisplaySwitcher.Config;
using DisplaySwitcher.Display;
using DisplaySwitcher.Gui.ViewModels;
using DisplaySwitcher.Logging;
using DisplaySwitcher.Profiles;
using DisplaySwitcher.Sunshine;
using MessageBox = System.Windows.MessageBox;

namespace DisplaySwitcher.Gui;

public sealed record PrepCommandRow(string Owner, string DoLine, string UndoLine);

public partial class MainWindow : Window
{
    private readonly RunLogger _log;
    private readonly ProfileEditorViewModel _editor;
    private AppSettings _settings;
    private string? _sunshineConfigPath;
    private bool _updatingSelection;
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load(AppPaths.SettingsFile, out _);
        _log = new RunLogger(AppPaths.LogDirectory, "gui", _settings.LogRetentionDays);
        _editor = new ProfileEditorViewModel(_log);

        DataContext = _editor;

        Arrangement.SelectionChanged += (_, monitor) => SelectMonitor(monitor);
        Arrangement.LayoutChanged += (_, _) =>
        {
            _editor.Renumber();
            _editor.SetStatus("Layout changed. Press Save profile to keep it.");
            RefreshStatusBar();
        };

        _editor.LayoutChanged += (_, _) => RedrawCanvas();
        _editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileEditorViewModel.StatusMessage))
            {
                RefreshStatusBar();
            }
        };

        Loaded += OnLoaded;
        Closed += (_, _) => _log.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Selecting the item drives OnProfileChanged, which does the actual load. Doing it here
        // rather than through a two-way binding keeps the order of events predictable.
        _ready = true;
        ProfileSelector.SelectedItem = _editor.CurrentProfile;

        LoadSettingsIntoUi();
        RefreshSunshine();
        RefreshDiagnostics();
        ReloadLog();
    }

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ProfileSelector.SelectedItem is not ProfileOption profile)
        {
            return;
        }

        RunBusy(() =>
        {
            _editor.CurrentProfile = profile;
            _editor.LoadProfile(profile.Key);
        });

        RedrawCanvas();
        RefreshStatusBar();
    }

    // ------------------------------------------------------------------ profiles

    private void RedrawCanvas()
    {
        Arrangement.SetMonitors(_editor.Monitors.ToList());
        Arrangement.Select(_editor.SelectedMonitor);
        SyncSelectionPanel();
    }

    private void SelectMonitor(MonitorViewModel? monitor)
    {
        _editor.SelectedMonitor = monitor;
        SyncSelectionPanel();
    }

    private void SyncSelectionPanel()
    {
        _updatingSelection = true;

        var monitor = _editor.SelectedMonitor;

        if (monitor is null)
        {
            SelectedTitle.Text = "No display selected";
            SelectedDetail.Text = "Click a display above, or one of the unused displays below.";
            SelectedEditor.IsEnabled = false;
            ResolutionCombo.ItemsSource = null;
            RefreshCombo.ItemsSource = null;
            _updatingSelection = false;
            return;
        }

        SelectedTitle.Text = $"{monitor.Number}.  {monitor.Label}";
        SelectedDetail.Text = $"{monitor.DetailLine}   \u2022   {monitor.StatusText}";
        SelectedEditor.IsEnabled = true;

        UseDisplayCheck.IsChecked = monitor.IsActive;
        UseDisplayCheck.IsEnabled = monitor.IsConnected;
        PrimaryCheck.IsChecked = monitor.IsPrimary;
        PrimaryCheck.IsEnabled = monitor.IsActive;

        ResolutionCombo.ItemsSource = monitor.Resolutions;
        ResolutionCombo.SelectedItem = monitor.SelectedResolution;
        RefreshCombo.ItemsSource = monitor.RefreshRates;
        RefreshCombo.SelectedItem = monitor.SelectedRefreshHz;

        var hasModes = monitor.Resolutions.Count > 0;
        ResolutionCombo.IsEnabled = hasModes;
        RefreshCombo.IsEnabled = hasModes;

        _updatingSelection = false;
    }

    private void RefreshStatusBar()
    {
        StatusText.Text = _editor.StatusMessage;

        var (background, border) = _editor.StatusSeverity switch
        {
            "error" => ("#FFFDF3F2", "#FFF3C9C4"),
            "warning" => ("#FFFFF9F0", "#FFF2DFC0"),
            "success" => ("#FFF1F8F1", "#FFC9E3C9"),
            _ => ("#FFF2F6FB", "#FFD6E4F0")
        };

        StatusBar.Background = (Brush)new BrushConverter().ConvertFromString(background)!;
        StatusBar.BorderBrush = (Brush)new BrushConverter().ConvertFromString(border)!;
    }

    private void OnTrayMonitorClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MonitorViewModel monitor })
        {
            SelectMonitor(monitor);
            Arrangement.Select(monitor);
        }
    }

    private void OnUseDisplayToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingSelection || _editor.SelectedMonitor is not { } monitor)
        {
            return;
        }

        _editor.ToggleActive(monitor, UseDisplayCheck.IsChecked == true);
        RedrawCanvas();
        RefreshStatusBar();
    }

    private void OnPrimaryToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingSelection || _editor.SelectedMonitor is not { } monitor)
        {
            return;
        }

        if (PrimaryCheck.IsChecked == true)
        {
            _editor.SetPrimary(monitor);
        }
        else
        {
            // Windows always has exactly one main display, so unticking is not a real state.
            PrimaryCheck.IsChecked = true;
        }

        RedrawCanvas();
    }

    private void OnResolutionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection || _editor.SelectedMonitor is not { } monitor)
        {
            return;
        }

        if (ResolutionCombo.SelectedItem is ResolutionOption option)
        {
            monitor.SelectedResolution = option;
            RefreshCombo.SelectedItem = monitor.SelectedRefreshHz;
            RedrawCanvas();
        }
    }

    private void OnRefreshChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection || _editor.SelectedMonitor is not { } monitor)
        {
            return;
        }

        if (RefreshCombo.SelectedItem is uint hz)
        {
            monitor.SelectedRefreshHz = hz;
            SelectedDetail.Text = $"{monitor.DetailLine}   \u2022   {monitor.StatusText}";
        }
    }

    private void OnImportLive(object sender, RoutedEventArgs e)
    {
        RunBusy(() => _editor.ImportLive());
        RedrawCanvas();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_editor.Save())
        {
            RedrawCanvas();
        }

        RefreshStatusBar();
    }

    private void OnPreview(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "This applies the profile for real, so your screens will change now.\n\n" +
            "If a display does not come back, wait 15 seconds and Windows will not revert automatically \u2013 " +
            "you can switch back with the other profile.",
            "Apply this profile now?",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        var exitCode = ExitCodes.UnexpectedError;
        RunBusy(() => exitCode = _editor.Preview(_settings));

        _editor.SetStatus(
            exitCode switch
            {
                ExitCodes.Success => "Applied and verified against the live topology.",
                ExitCodes.VerificationFailed => "Windows accepted the change but the displays did not end up as requested. See the log in Settings.",
                ExitCodes.MonitorMissing => "A display this profile needs is not connected right now.",
                ExitCodes.BadArguments => _editor.StatusMessage,
                _ => "The switch failed unexpectedly. See the log in Settings."
            },
            exitCode == ExitCodes.Success ? "success" : "error");

        ReloadLog();
        RefreshDiagnostics();
        RefreshStatusBar();
    }

    private void RunBusy(Action action)
    {
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.Error("GUI operation failed", ex);
            _editor.SetStatus(ex.Message, "error");
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    // ------------------------------------------------------------------ sunshine

    private void RefreshSunshine()
    {
        if (_sunshineConfigPath is null)
        {
            var detected = SunshineConfigService.Detect();
            _sunshineConfigPath = detected?.ConfigPath;

            SunshineStatus.Text = detected is null
                ? "Sunshine was not found automatically. Browse to its sunshine.conf, usually in C:\\Program Files\\Sunshine\\config."
                : $"Found via {detected.DiscoveredVia}.";
        }

        SunshineConfigPath.Text = _sunshineConfigPath ?? "(not found)";

        if (_sunshineConfigPath is null || !File.Exists(_sunshineConfigPath))
        {
            PrepCommandList.ItemsSource = null;
            NoPrepCommands.Visibility = Visibility.Visible;
            InstallHookButton.IsEnabled = false;
            return;
        }

        InstallHookButton.IsEnabled = true;

        var commands = SunshineConfigService.ReadPrepCommands(_sunshineConfigPath);

        PrepCommandList.ItemsSource = commands
            .Select(c => new PrepCommandRow(
                c.IsOurs ? "DisplaySwitcher (this app)" : "Another tool \u2013 left untouched",
                $"on connect:  {(c.Do.Length > 0 ? c.Do : "(nothing)")}",
                $"on disconnect:  {(c.Undo.Length > 0 ? c.Undo : "(nothing)")}"))
            .ToList();

        NoPrepCommands.Visibility = commands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var installed = commands.Any(c => c.IsOurs);
        InstallHookButton.Content = installed ? "Update Sunshine entry" : "Wire into Sunshine";

        if (installed)
        {
            SunshineStatus.Text += "  DisplaySwitcher is wired in and will run on every stream.";
        }
        else
        {
            SunshineStatus.Text += "  DisplaySwitcher is not wired in yet.";
        }

        if (!ProfileStore.Exists(ProfileStore.Streaming) || !ProfileStore.Exists(ProfileStore.Desktop))
        {
            SunshineStatus.Text += "  Save both profiles first, or the hook will have nothing to apply.";
        }
    }

    private void OnRefreshSunshine(object sender, RoutedEventArgs e) => RefreshSunshine();

    private void OnBrowseSunshine(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select sunshine.conf",
            Filter = "Sunshine configuration (*.conf)|*.conf|All files (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(_sunshineConfigPath ?? string.Empty) is { Length: > 0 } dir
                ? dir
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };

        if (dialog.ShowDialog() == true)
        {
            _sunshineConfigPath = dialog.FileName;
            SunshineStatus.Text = "Using the file you selected.";
            RefreshSunshine();
        }
    }

    private void OnInstallHook(object sender, RoutedEventArgs e) => RunElevatedVerb("install-hook", "wire DisplaySwitcher into Sunshine");

    private void OnRemoveHook(object sender, RoutedEventArgs e) => RunElevatedVerb("remove-hook", "remove DisplaySwitcher from Sunshine");

    /// <summary>
    /// sunshine.conf lives under Program Files, so the edit is delegated to a short-lived elevated
    /// copy of this same exe rather than making the whole GUI require administrator rights.
    /// </summary>
    private void RunElevatedVerb(string verb, string description)
    {
        try
        {
            var startInfo = new ProcessStartInfo(AppPaths.DisplaySwitcherExe, verb)
            {
                UseShellExecute = true,
                Verb = "runas"
            };

            using var process = Process.Start(startInfo);
            process?.WaitForExit(60_000);

            var succeeded = process is { HasExited: true, ExitCode: ExitCodes.Success };

            MessageBox.Show(
                succeeded
                    ? $"Done. Restart Sunshine so it re-reads its configuration."
                    : $"Could not {description}. The log in the Settings tab has the detail.",
                "Sunshine configuration",
                MessageBoxButton.OK,
                succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            // Cancelling the UAC prompt lands here; there is nothing to report.
            _log.Warn($"Elevated '{verb}' did not run: {ex.Message}");
        }

        RefreshSunshine();
        ReloadLog();
    }

    private void OnRestartSunshine(object sender, RoutedEventArgs e)
    {
        var result = SunshineConfigService.RestartSunshine();

        MessageBox.Show(
            result.Message,
            "Restart Sunshine",
            MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    // ------------------------------------------------------------------ diagnostics

    private void RefreshDiagnostics()
    {
        var text = new StringBuilder();

        try
        {
            var inventory = MonitorInventory.Read(_log);

            text.AppendLine($"DisplaySwitcher diagnostics  -  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            text.AppendLine($"Data folder: {AppPaths.Root}");
            text.AppendLine($"Executable:  {AppPaths.DisplaySwitcherExe}");
            text.AppendLine($"Profiles:    normal={(ProfileStore.Exists(ProfileStore.Desktop) ? "saved" : "missing")}, " +
                            $"streaming={(ProfileStore.Exists(ProfileStore.Streaming) ? "saved" : "missing")}");
            text.AppendLine();
            text.AppendLine($"{inventory.Count} connected monitors " +
                            $"({inventory.Count(i => i.IsActive)} active).");
            text.AppendLine();

            foreach (var item in inventory)
            {
                var m = item.Current;
                text.AppendLine($"[{(m.Active ? "ACTIVE  " : "inactive")}] {m.Label}");
                text.AppendLine($"    devicePath  : {m.DevicePath}");
                text.AppendLine($"    gdiName     : {(m.GdiDeviceName.Length > 0 ? m.GdiDeviceName : "(none)")}");
                text.AppendLine($"    sourceId    : {m.SourceId}");
                text.AppendLine($"    outputTech  : {m.OutputTechnology}   primary: {m.IsPrimary}");
                text.AppendLine($"    mode        : {m.DescribeMode()}");
                text.AppendLine($"    modes known : {item.Modes.Count}" +
                                (item.Modes.Count > 0 ? $"   best: {item.Modes[0]}" : string.Empty));
                text.AppendLine();
            }
        }
        catch (Exception ex)
        {
            text.AppendLine($"Could not read the display topology: {ex}");
        }

        DiagnosticsText.Text = text.ToString();
    }

    private void OnRefreshDisplays(object sender, RoutedEventArgs e) => RefreshDiagnostics();

    private void OnCopyDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DiagnosticsText.Text);
            MessageBox.Show("Diagnostics copied. Paste them into a GitHub issue.", "Copied",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _log.Warn($"Clipboard copy failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ settings

    private void LoadSettingsIntoUi()
    {
        VerifyDelayBox.Text = _settings.VerifyDelaySeconds.ToString();
        MaxRetriesBox.Text = _settings.MaxRetries.ToString();
        RetryDelayBox.Text = _settings.RetryDelaySeconds.ToString();
        LogRetentionBox.Text = _settings.LogRetentionDays.ToString();
        DdcCiCheck.IsChecked = _settings.EnableDdcCiEscalation;
    }

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        var updated = new AppSettings
        {
            VerifyDelaySeconds = ParseOr(VerifyDelayBox.Text, _settings.VerifyDelaySeconds),
            MaxRetries = ParseOr(MaxRetriesBox.Text, _settings.MaxRetries),
            RetryDelaySeconds = ParseOr(RetryDelayBox.Text, _settings.RetryDelaySeconds),
            LogRetentionDays = ParseOr(LogRetentionBox.Text, _settings.LogRetentionDays),
            EnableDdcCiEscalation = DdcCiCheck.IsChecked == true,
            DdcCiPowerCycleSeconds = _settings.DdcCiPowerCycleSeconds,
            DdcCiSettleSeconds = _settings.DdcCiSettleSeconds
        };

        try
        {
            updated.Save(AppPaths.SettingsFile);
            _settings = updated;
            LoadSettingsIntoUi();
            SettingsStatus.Text = $"Saved to {AppPaths.SettingsFile}";
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"Could not save: {ex.Message}";
        }
    }

    private static int ParseOr(string text, int fallback) =>
        int.TryParse(text.Trim(), out var value) ? value : fallback;

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"Could not open the folder: {ex.Message}";
        }
    }

    private void OnReloadLog(object sender, RoutedEventArgs e) => ReloadLog();

    private void ReloadLog()
    {
        try
        {
            if (!File.Exists(_log.LogPath))
            {
                LogText.Text = "(no log yet)";
                return;
            }

            // The log is opened with FileShare.ReadWrite by the writer, so this read is safe.
            using var stream = new FileStream(_log.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var lines = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > 400)
                {
                    lines.Dequeue();
                }
            }

            LogText.Text = string.Join(Environment.NewLine, lines);

            // Scrolling only works once the TextBox has been laid out, which has not happened yet
            // on the first load, so defer to after the current layout pass.
            Dispatcher.BeginInvoke(new Action(() => LogText.ScrollToEnd()), DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            LogText.Text = $"Could not read {_log.LogPath}: {ex.Message}";
        }
    }
}
