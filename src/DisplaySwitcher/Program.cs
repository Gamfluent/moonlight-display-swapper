using System.IO;
using System.Runtime.InteropServices;
using DisplaySwitcher.Cli;

namespace DisplaySwitcher;

/// <summary>
/// Single entry point for both faces of the app: no arguments opens the configuration window,
/// any argument runs the headless command used by Sunshine's prep commands.
/// </summary>
public static class Program
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            return RunGui();
        }

        // The app is built as a Windows subsystem binary so Sunshine never flashes a console
        // window at the start and end of every stream. When a human runs it from a terminal we
        // borrow that terminal, then rebuild the console streams because .NET has already
        // bound Console.Out to a null device by this point.
        if (AttachConsole(AttachParentProcess))
        {
            try
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            }
            catch (IOException)
            {
                // No usable console; the log file still records everything.
            }
        }

        return CliRunner.Run(args);
    }

    private static int RunGui()
    {
        try
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        catch (Exception ex)
        {
            // A failure this early is before any window or logger exists, so it would otherwise be
            // completely silent: the process just vanishes with no window and no log entry.
            ReportStartupFailure(ex);
            return ExitCodes.UnexpectedError;
        }
    }

    private static void ReportStartupFailure(Exception ex)
    {
        try
        {
            var path = Path.Combine(AppPaths.LogDirectory, "startup-error.log");
            Directory.CreateDirectory(AppPaths.LogDirectory);
            File.AppendAllText(path, $"{DateTime.Now:u}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Nothing further we can usefully do.
        }

        try
        {
            System.Windows.MessageBox.Show(
                $"DisplaySwitcher could not start.\n\n{ex.Message}",
                "DisplaySwitcher",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        catch
        {
            // No UI available.
        }
    }
}
