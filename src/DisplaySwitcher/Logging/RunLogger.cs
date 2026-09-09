using System.Globalization;
using System.Text;

namespace DisplaySwitcher.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

/// <summary>
/// Append-only logger for a single run. Everything also goes to stdout so Sunshine's own
/// log captures it, and so an interactive run needs no second window.
/// </summary>
public sealed class RunLogger : IDisposable
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";
    private const long MaxFileBytes = 8L * 1024 * 1024;

    private readonly string _logPath;
    private readonly StreamWriter? _writer;
    private readonly object _gate = new();

    public RunLogger(string logDirectory, string mode, int retentionDays)
    {
        _logPath = Path.Combine(logDirectory, "displayswitcher.log");

        try
        {
            Directory.CreateDirectory(logDirectory);
            PruneOldEntries(_logPath, retentionDays);
            _writer = new StreamWriter(new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true
            };
        }
        catch (Exception ex)
        {
            // A logging failure must never prevent the display switch itself.
            Console.Error.WriteLine($"WARNING: could not open log file {_logPath}: {ex.Message}");
            _writer = null;
        }

        Write(LogLevel.Info, string.Empty);
        Write(LogLevel.Info, $"=== RUN mode={mode} pid={Environment.ProcessId} host={Environment.MachineName} ===");
    }

    public string LogPath => _logPath;

    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);
    public void Error(string message) => Write(LogLevel.Error, message);

    public void Error(string message, Exception ex) =>
        Write(LogLevel.Error, $"{message}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    public void Write(LogLevel level, string message)
    {
        var line = message.Length == 0
            ? string.Empty
            : $"{DateTime.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture)} [{level.ToString().ToUpperInvariant(),-5}] {message}";

        lock (_gate)
        {
            try
            {
                _writer?.WriteLine(line);
            }
            catch
            {
                // Ignore: see constructor note.
            }

            if (level == LogLevel.Error)
            {
                Console.Error.WriteLine(line);
            }
            else
            {
                Console.WriteLine(line);
            }
        }
    }

    /// <summary>
    /// Drops log lines older than the retention window. Continuation lines (stack traces and the
    /// like) carry no timestamp, so they inherit the decision made for the line above them.
    /// </summary>
    private static void PruneOldEntries(string path, int retentionDays)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var cutoff = DateTime.Now.AddDays(-retentionDays);
        var info = new FileInfo(path);

        if (info.LastWriteTime >= cutoff && info.Length < MaxFileBytes)
        {
            return;
        }

        try
        {
            var kept = new StringBuilder();
            var keepingCurrent = true;

            foreach (var line in File.ReadLines(path))
            {
                if (line.Length >= TimestampFormat.Length &&
                    DateTime.TryParseExact(
                        line[..TimestampFormat.Length],
                        TimestampFormat,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var stamp))
                {
                    keepingCurrent = stamp >= cutoff;
                }

                if (keepingCurrent)
                {
                    kept.AppendLine(line);
                }
            }

            File.WriteAllText(path, kept.ToString());
        }
        catch
        {
            // If pruning fails the log simply keeps growing; that is preferable to losing the run.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
    }
}
