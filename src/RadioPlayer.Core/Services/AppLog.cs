using System.IO;
using System.Text;

namespace RadioPlayer.Services;

/// <summary>
/// The app's troubleshooting log: a plain-text file under %LocalAppData%\RadioPlayer\logs\.
///
/// Everything used to go through <c>Debug.WriteLine</c>, which only exists when a debugger is
/// attached — so a failure in a normally-launched copy left nothing at all behind to look at.
/// The call sites keep their existing <c>[Category] message</c> convention; this adds a
/// timestamp, a level and the thread, and puts it somewhere readable after the fact.
///
/// Deliberately tiny and dependency-free, matching the rest of this codebase. Every write is
/// wrapped and failures are swallowed in silence: a logger must never be able to break the thing
/// it's logging, and it certainly must never try to log its own failure.
///
/// NOTE: this class must not <c>using System.Diagnostics</c> — its own <see cref="Debug"/> method
/// would then shadow <c>System.Diagnostics.Debug</c>, so the mirror call below is fully qualified.
/// </summary>
public static class AppLog
{
    private const int KeepDays = 7;

    private static readonly object Gate = new();
    private static string? _path;
    private static bool _setupFailed;
    private static bool _suppressed;

    /// <summary>The current log file, or null if logging couldn't be set up.</summary>
    public static string? FilePath
    {
        get { lock (Gate) return EnsureFile(); }
    }

    /// <summary>
    /// Stops this process writing to the shared log file. For the test host: a test run otherwise
    /// appends to the same %LocalAppData% file the real app uses, interleaving hundreds of lines
    /// with a live session's diagnostics — precisely when someone is reading that file to work out
    /// what a session did. Cannot be undone; nothing but a test host should ever call it.
    /// </summary>
    public static void Suppress()
    {
        lock (Gate) _suppressed = true;
    }

    public static void Debug(string message) => Write("DBG", message);
    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);

    /// <summary>Logs an error, with the exception's full detail (type, message, stack) when given
    /// one — the stack is usually the whole point of going to the log in the first place.</summary>
    public static void Error(string message, Exception? ex = null) =>
        Write("ERR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    /// <summary>Writes a run header. Called once at startup; logging works without it.</summary>
    public static void BeginSession(string appVersion)
    {
        Write("INF", new string('─', 60));
        Write("INF", $"Crystal Radio {appVersion} starting · .NET {Environment.Version} · "
                     + $"{Environment.OSVersion.VersionString}");
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = EnsureFile();
                if (path is null) return;

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {level}  "
                           + $"t{Environment.CurrentManagedThreadId,-3} {message}";

                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);

                // Keep the debugger's Output window working exactly as before.
                System.Diagnostics.Debug.WriteLine(message);
            }
        }
        catch
        {
            // Never throw out of logging, and never try to log the logging failure.
        }
    }

    /// <summary>Resolves (and on first use creates) today's log file. Caller holds the lock.</summary>
    private static string? EnsureFile()
    {
        if (_suppressed)
            return null;
        if (_path is not null || _setupFailed)
            return _path;

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
                "RadioPlayer", "logs");
            Directory.CreateDirectory(dir);
            Prune(dir);
            // One file per day, appended across runs — a session header separates the runs.
            _path = Path.Combine(dir, $"app-{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            _setupFailed = true; // don't retry on every call
        }
        return _path;
    }

    private static void Prune(string dir)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-KeepDays);
            foreach (var file in new DirectoryInfo(dir).GetFiles("app-*.log"))
                if (file.LastWriteTimeUtc < cutoff)
                    try { file.Delete(); } catch { /* best effort */ }
        }
        catch { /* best effort */ }
    }
}
