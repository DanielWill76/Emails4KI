using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

/// <summary>
/// Zentrale Logging-Fassade auf Basis von Microsoft.Extensions.Logging und DailyFileLoggerProvider.
/// </summary>
public static class Logger
{
    private static readonly object SyncRoot = new();
    private static ILoggerFactory? loggerFactory;
    private static DailyFileLoggerProvider? fileProvider;
    private static ILogger? logger;

    public static bool EnableConsoleOutput { get; set; } = false;
    public static string? CurrentLogPath => fileProvider?.LogFilePath;

    /// <summary>
    /// Stellt sicher, dass im Fehlerfall (z. B. ungültige JSON-Konfiguration beim Start)
    /// zumindest das Fallback-Logging aktiv ist.
    /// </summary>
    public static void EnsureFallbackInitialized(string baseDirectory)
    {
        lock (SyncRoot)
        {
            if (fileProvider is not null)
                return;
        }

        InitCore(baseDirectory, new LoggingSettings(), loggingSection: null);
    }

    /// <summary>
    /// Initialisiert das Logging vollständig anhand von "Emails4KI.json"
    /// (Abschnitte "Logging" und "FileLogging") sowie Umgebungsvariablen.
    /// </summary>
    public static void Init(IConfiguration configuration, string baseDirectory)
    {
        var fileSettings = LoggingSettings.BindFrom(configuration);
        var loggingSection = configuration.GetSection("Logging");
        InitCore(baseDirectory, fileSettings, loggingSection.Exists() ? loggingSection : null);
    }

    private static void InitCore(string baseDirectory, LoggingSettings settings, IConfigurationSection? loggingSection)
    {
        lock (SyncRoot)
        {
            CloseInternal();

            var provider = new DailyFileLoggerProvider(baseDirectory, settings, "Emails4KI");
            var factory = LoggerFactory.Create(builder =>
            {
                if (loggingSection is not null)
                    builder.AddConfiguration(loggingSection);
                else
                    builder.SetMinimumLevel(LogLevel.Information);

                builder.AddProvider(provider);
            });

            fileProvider = provider;
            loggerFactory = factory;
            logger = factory.CreateLogger("Emails4KI");
        }
    }

    public static void Log(string msg) => Write(LogLevel.Information, msg, null);
    public static void LogDebug(string msg) => Write(LogLevel.Debug, msg, null);
    public static void LogWarning(string msg) => Write(LogLevel.Warning, msg, null);
    public static void LogError(string msg, Exception? ex = null) => Write(LogLevel.Error, msg, ex);
    public static void LogCritical(string msg, Exception? ex = null) => Write(LogLevel.Critical, msg, ex);

    public static void LogUnhandledException(string context, Exception ex)
    {
        lock (SyncRoot)
        {
            if (fileProvider is not null)
            {
                fileProvider.LogUnhandledException(context, ex);
                return;
            }
        }

        Write(LogLevel.Critical, context, ex);
    }

    private static void Write(LogLevel level, string msg, Exception? ex)
    {
        ILogger? currentLogger;
        lock (SyncRoot)
        {
            currentLogger = logger;
        }

        if (currentLogger is not null)
        {
            currentLogger.Log(level, ex, "{Message}", msg);
        }

        if (EnableConsoleOutput)
        {
            Console.WriteLine(ex is null ? msg : $"{msg}: {ex.Message}");
        }
    }

    public static void Close()
    {
        lock (SyncRoot)
        {
            CloseInternal();
        }
    }

    private static void CloseInternal()
    {
        // Zuerst LoggerFactory schließen, anschließend den FileProvider sauber mit Stopp-Marker beenden
        if (loggerFactory is not null)
        {
            try
            {
                loggerFactory.Dispose();
            }
            catch
            {
                // Ignorieren
            }
            finally
            {
                loggerFactory = null;
                logger = null;
            }
        }

        if (fileProvider is not null)
        {
            try
            {
                fileProvider.Dispose();
            }
            catch
            {
                // Ignorieren
            }
            finally
            {
                fileProvider = null;
            }
        }
    }
}
