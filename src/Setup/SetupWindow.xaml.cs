using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using DisplaySwitcher.Profiles;
using DisplaySwitcher.Sunshine;

namespace DisplaySwitcher.Setup;

public sealed record PrepRow(string Owner, string DoLine, string UndoLine);

public partial class SetupWindow : Window
{
    private string? _configPath;

    public SetupWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            CheckApplication();
            Detect();
        };
    }

    /// <summary>
    /// Setup does not install anything, so DisplaySwitcher.exe must already be sitting beside it.
    /// This is the single most common way a portable release goes wrong, so it is checked first.
    /// </summary>
    private void CheckApplication()
    {
        var exePath = AppPaths.DisplaySwitcherExe;

        if (File.Exists(exePath))
        {
            ExeStatus.Text = $"Found at {exePath}. Sunshine will be pointed at this exact path, so keep the folder where it is.";
            OpenAppButton.IsEnabled = true;
        }
        else
        {
            ExeStatus.Text = $"DisplaySwitcher.exe was not found next to Setup.exe (expected {exePath}). " +
                             "Keep both files together in the same folder and run Setup again.";
            ExeStatus.Foreground = (Brush)FindResource("DangerBrush");
            OpenAppButton.IsEnabled = false;
            InstallButton.IsEnabled = false;
        }

        var hasStreaming = ProfileStore.Exists(ProfileStore.Streaming);
        var hasDesktop = ProfileStore.Exists(ProfileStore.Desktop);

        if (hasStreaming && hasDesktop)
        {
            ProfileStatus.Text = "Both display profiles are saved, so the hook has something to apply.";
            ProfileStatus.Foreground = (Brush)FindResource("SuccessBrush");
        }
        else
        {
            var missing = (hasStreaming, hasDesktop) switch
            {
                (false, false) => "Neither profile has been saved yet",
                (false, true) => "The streaming profile has not been saved yet",
                _ => "The desktop profile has not been saved yet"
            };

            ProfileStatus.Text = $"{missing}. You can wire up Sunshine now, but open DisplaySwitcher and save both " +
                                 "profiles before you stream, or the hook will have nothing to switch to.";
            ProfileStatus.Foreground = (Brush)FindResource("WarningBrush");
        }
    }

    private void Detect()
    {
        if (_configPath is null)
        {
            var install = SunshineConfigService.Detect();

            if (install is null)
            {
                DetectionStatus.Text = "Sunshine was not found automatically. Use Browse to point at sunshine.conf, " +
                                       "usually C:\\Program Files\\Sunshine\\config\\sunshine.conf.";
                DetectionStatus.Foreground = (Brush)FindResource("WarningBrush");
                ConfigPathBox.Text = "(not found)";
                SetActionsEnabled(false);
                return;
            }

            _configPath = install.ConfigPath;
            DetectionStatus.Text = $"Detected automatically via the {install.DiscoveredVia}.";
            DetectionStatus.Foreground = (Brush)FindResource("TextSubtle");
        }

        ConfigPathBox.Text = _configPath;
        SetActionsEnabled(true);
        RefreshPrepList();
    }

    private void SetActionsEnabled(bool enabled)
    {
        InstallButton.IsEnabled = enabled && File.Exists(AppPaths.DisplaySwitcherExe);
        RemoveButton.IsEnabled = enabled;
        RestartButton.IsEnabled = SunshineConfigService.ServiceExists();
    }

    private void RefreshPrepList()
    {
        if (_configPath is null || !File.Exists(_configPath))
        {
            PrepList.ItemsSource = null;
            EmptyPrepNote.Visibility = Visibility.Visible;
            return;
        }

        var commands = SunshineConfigService.ReadPrepCommands(_configPath);

        PrepList.ItemsSource = commands
            .Select(c => new PrepRow(
                c.IsOurs ? "DisplaySwitcher" : "Another tool \u2013 will be left exactly as it is",
                $"on connect:  {(c.Do.Length > 0 ? c.Do : "(nothing)")}",
                $"on disconnect:  {(c.Undo.Length > 0 ? c.Undo : "(nothing)")}"))
            .ToList();

        EmptyPrepNote.Visibility = commands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var installed = commands.Any(c => c.IsOurs);
        InstallButton.Content = installed ? "Update the entry" : "Wire into Sunshine";
        RemoveButton.IsEnabled = installed;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select sunshine.conf",
            Filter = "Sunshine configuration (*.conf)|*.conf|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _configPath = dialog.FileName;
        DetectionStatus.Text = "Using the file you selected.";
        DetectionStatus.Foreground = (Brush)FindResource("TextSubtle");
        Detect();
    }

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_configPath is null)
        {
            return;
        }

        var result = SunshineConfigService.InstallHook(_configPath, AppPaths.DisplaySwitcherExe);
        Report(result);
        RefreshPrepList();

        if (result.Success)
        {
            OfferRestart();
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_configPath is null)
        {
            return;
        }

        var result = SunshineConfigService.RemoveHook(_configPath);
        Report(result);
        RefreshPrepList();

        if (result.Success)
        {
            OfferRestart();
        }
    }

    private void OfferRestart()
    {
        if (!SunshineConfigService.ServiceExists())
        {
            return;
        }

        var answer = MessageBox.Show(
            "Sunshine reads its configuration only when it starts. Restart it now?\n\n" +
            "Any stream that is running will be dropped.",
            "Restart Sunshine",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
        {
            Report(SunshineConfigService.RestartSunshine());
        }
    }

    private void OnRestart(object sender, RoutedEventArgs e) => Report(SunshineConfigService.RestartSunshine());

    private void OnOpenApp(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.DisplaySwitcherExe) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Report(new HookResult(false, $"Could not start DisplaySwitcher: {ex.Message}"));
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void Report(HookResult result)
    {
        ResultText.Text = result.BackupPath is null
            ? result.Message
            : $"{result.Message}   Backup: {Path.GetFileName(result.BackupPath)}";

        var (background, border) = result.Success
            ? ("#FFF1F8F1", "#FFC9E3C9")
            : ("#FFFDF3F2", "#FFF3C9C4");

        var converter = new BrushConverter();
        ResultBar.Background = (Brush)converter.ConvertFromString(background)!;
        ResultBar.BorderBrush = (Brush)converter.ConvertFromString(border)!;
    }
}
