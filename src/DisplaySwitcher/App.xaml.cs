using System.Windows;
using System.Windows.Threading;

namespace DisplaySwitcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);

        // The window is created here rather than through StartupUri so that a failure in its
        // constructor surfaces as an error instead of a live process with no window.
        try
        {
            MainWindow = new Gui.MainWindow();
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            ReportAndQuit(ex);
        }
    }

    private void ReportAndQuit(Exception ex)
    {
        try
        {
            var path = System.IO.Path.Combine(AppPaths.LogDirectory, "startup-error.log");
            System.IO.Directory.CreateDirectory(AppPaths.LogDirectory);
            System.IO.File.AppendAllText(path, $"{DateTime.Now:u}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Nothing further we can usefully do.
        }

        MessageBox.Show(
            $"DisplaySwitcher could not open its window.\n\n{ex.Message}",
            "DisplaySwitcher",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        Shutdown(ExitCodes.UnexpectedError);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"{e.Exception.Message}\n\nThe log in the Settings tab has the full detail.",
            "DisplaySwitcher hit an unexpected problem",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
