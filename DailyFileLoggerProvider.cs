using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="ILoggerProvider"/> für das tägliche Datei-Logging im einheitlichen Schema
/// "&lt;AppName&gt;-yyyy-MM-dd.log" (hier: "Emails4KI-yyyy-MM-dd.log").
///
/// Eigenschaften:
/// - Thread-sicherer Zugriff mit FileShare.ReadWrite (Logs bleiben im Editor lesbar).
/// - Einheitliche Zeitstempel &amp; Level-Tags ([HH:mm:ss INF/WRN/ERR/CRT]) inkl. eingerückter Stacktraces.
/// - Automatische Mitternachts-Rotation und Bereinigung älterer Logdateien (RetentionDays).
/// - Saubere Start-, Tageswechsel- und Stopp-Marker sowie direkter Handler für UnhandledException.
/// </summary>
public sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private const string FileSuffix = ".log";

    private readonly object _syncRoot = new();
    private readonly string _filePrefix;
    private readonly string _logDirectory;
    private readonly int _retentionDays;

    private DateTime _currentLogDate = DateTime.MinValue;
    private StreamWriter? _fileWriter;
    private bool _disposed;

    /// <summary>Voller Pfad der aktuell verwendeten Logdatei (null, falls keine Datei geöffnet werden konnte).</summary>
    public string? LogFilePath { get; private set; }

    /// <summary>Aufgelöstes Log-Verzeichnis.</summary>
    public string LogDirectory => _logDirectory;

    public DailyFileLoggerProvider(
        string baseDirectory,
        LoggingSettings settings,
        string appName = "Emails4KI")
    {
        _filePrefix = $"{(string.IsNullOrWhiteSpace(appName) ? "Emails4KI" : appName.Trim())}-";
        _logDirectory = settings.ResolveDirectory(baseDirectory);
        _retentionDays = settings.RetentionDays;

        lock (_syncRoot)
        {
            EnsureCurrentDateWriterLocked(isInitialStart: true);
        }
    }

    public ILogger CreateLogger(string categoryName) => new DailyFileLogger(this, categoryName);

    /// <summary>
    /// Schreibt eine unbehandelte Ausnahme direkt und thread-sicher in die Logdatei
    /// (z. B. aus AppDomain.CurrentDomain.UnhandledException oder Application.ThreadException).
    /// </summary>
    public void LogUnhandledException(string context, Exception exception)
    {
        WriteEntry(LogLevel.Critical, "UnhandledException", context, exception);
    }

    private void WriteEntry(LogLevel logLevel, string categoryName, string message, Exception? exception)
    {
        if (logLevel == LogLevel.None)
            return;

        lock (_syncRoot)
        {
            if (_disposed)
                return;

            var writer = EnsureCurrentDateWriterLocked();
            if (writer is null)
                return;

            try
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                string levelTag = FormatLevel(logLevel);
                string header = $"[{timestamp} {levelTag}] ";

                if (!string.IsNullOrEmpty(message))
                {
                    var lines = message.Replace("\r\n", "\n").Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        writer.WriteLine(i == 0
                            ? $"{header}{lines[i]}"
                            : $"               {lines[i]}");
                    }
                }
                else if (exception is not null)
                {
                    writer.WriteLine($"{header}{categoryName}:");
                }

                if (exception is not null)
                {
                    var excLines = exception.ToString().Replace("\r\n", "\n").Split('\n');
                    foreach (var line in excLines)
                    {
                        writer.WriteLine($"               {line}");
                    }
                }
            }
            catch
            {
                // Logging darf die Anwendung niemals zum Absturz bringen.
            }
        }
    }

    private StreamWriter? EnsureCurrentDateWriterLocked(bool isInitialStart = false)
    {
        if (_disposed)
            return null;

        DateTime today = DateTime.Now.Date;
        if (_fileWriter is not null && today == _currentLogDate)
            return _fileWriter;

        try
        {
            _fileWriter?.Dispose();
            _fileWriter = null;

            Directory.CreateDirectory(_logDirectory);
            try
            {
                CleanupOldLogFiles(_logDirectory, _filePrefix, _retentionDays);
            }
            catch
            {
                // Fehler beim Bereinigen alter Logs darf das Öffnen der heutigen Logdatei nicht verhindern.
            }

            string fileName = $"{_filePrefix}{today:yyyy-MM-dd}{FileSuffix}";
            string path = Path.Combine(_logDirectory, fileName);
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };

            writer.WriteLine();
            writer.WriteLine(isInitialStart
                ? $"===== Start {DateTime.Now:yyyy-MM-dd HH:mm:ss} (v{GetApplicationVersion()}) ====="
                : $"===== Tageswechsel {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");

            _fileWriter = writer;
            _currentLogDate = today;
            LogFilePath = path;
        }
        catch
        {
            // Ignorieren, damit ein I/O-Fehler beim Loggen die Anwendung nicht beendet.
        }

        return _fileWriter;
    }

    private static void CleanupOldLogFiles(string logDirectory, string filePrefix, int retentionDays)
    {
        if (retentionDays <= 0 || !Directory.Exists(logDirectory))
            return;

        DateTime cutoff = DateTime.Now.Date.AddDays(-retentionDays);
        foreach (var file in Directory.GetFiles(logDirectory, $"{filePrefix}*{FileSuffix}"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string datePart = name.Length > filePrefix.Length ? name[filePrefix.Length..] : "";
            if (DateTime.TryParseExact(
                    datePart,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime fileDate)
                && fileDate < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Datei evtl. noch gesperrt – beim nächsten Start/Tageswechsel erneut versuchen.
                }
            }
        }
    }

    private static string GetApplicationVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(DailyFileLoggerProvider).Assembly;
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            int plusIndex = infoVersion.IndexOf('+');
            if (plusIndex >= 0 && infoVersion.Length > plusIndex + 8)
                return infoVersion[..(plusIndex + 8)];
            return infoVersion;
        }

        var version = assembly.GetName().Version;
        return version is not null ? $"{version.Major}.{version.Minor}.{version.Build}" : "unbekannt";
    }

    private static string FormatLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "INF",
    };

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                if (_fileWriter is not null)
                {
                    _fileWriter.WriteLine($"===== Stopp {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
                    _fileWriter.Flush();
                    _fileWriter.Dispose();
                }
            }
            catch
            {
                // Ignorieren
            }
            finally
            {
                _fileWriter = null;
            }
        }
    }

    private sealed class DailyFileLogger : ILogger
    {
        private readonly DailyFileLoggerProvider _provider;
        private readonly string _categoryName;

        public DailyFileLogger(DailyFileLoggerProvider provider, string categoryName)
        {
            _provider = provider;
            _categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            string message = formatter(state, exception);
            _provider.WriteEntry(logLevel, _categoryName, message, exception);
        }
    }
}
