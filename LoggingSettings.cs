using System;
using System.IO;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Einheitliche Konfiguration für das tägliche Datei-Logging.
/// Wird bevorzugt aus "Emails4KI.json" (Abschnitt "FileLogging") bzw.
/// Umgebungsvariablen ("EMAILS4KI_FileLogging__") gelesen.
/// Unterstützt Umgebungsvariablen (z. B. %LOCALAPPDATA%) sowie abwärtskompatible
/// Fallbacks auf "Logging" oder die älteren Felder "LogFile" / "LogRetentionDays".
/// </summary>
public sealed class LoggingSettings
{
    public const string DefaultDirectory = @"%LOCALAPPDATA%\Emails4KI\Logs";
    public const int DefaultRetentionDays = 30;

    /// <summary>
    /// Verzeichnis für die täglichen Logdateien. Unterstützt Umgebungsvariablen
    /// (z. B. %LOCALAPPDATA%\Emails4KI\Logs) sowie relative Pfade
    /// (relativ zum Verzeichnis der .exe) oder absolute Pfade (z. B. D:\Logs\Emails4KI).
    /// </summary>
    public string Directory { get; set; } = DefaultDirectory;

    /// <summary>
    /// Logdateien, deren Datum länger als diese Anzahl Tage zurückliegt,
    /// werden beim Programmstart sowie bei jedem Tageswechsel automatisch gelöscht.
    /// 0 deaktiviert die automatische Bereinigung.
    /// </summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    public static LoggingSettings BindFrom(IConfiguration configuration)
    {
        var fileLoggingSection = configuration.GetSection("FileLogging");
        if (fileLoggingSection.Exists())
        {
            var settings = fileLoggingSection.Get<LoggingSettings>() ?? new LoggingSettings();
            return Normalize(settings);
        }

        // Kompatibilitäts-Fallback 1: älterer Abschnitt "Logging" mit Directory/RetentionDays
        var loggingSection = configuration.GetSection("Logging");
        if (!string.IsNullOrWhiteSpace(loggingSection["Directory"]))
        {
            var settings = loggingSection.Get<LoggingSettings>() ?? new LoggingSettings();
            return Normalize(settings);
        }

        // Kompatibilitäts-Fallback 2: ältere Root-Schlüssel "LogFile" / "LogRetentionDays"
        string? legacyLogFile = configuration["LogFile"];
        string? legacyRetention = configuration["LogRetentionDays"];
        if (!string.IsNullOrWhiteSpace(legacyLogFile) || !string.IsNullOrWhiteSpace(legacyRetention))
        {
            var settings = new LoggingSettings();
            if (!string.IsNullOrWhiteSpace(legacyLogFile))
            {
                string expanded = Environment.ExpandEnvironmentVariables(legacyLogFile.Trim());
                if (Path.IsPathRooted(expanded) ||
                    expanded.Contains(Path.DirectorySeparatorChar) ||
                    expanded.Contains(Path.AltDirectorySeparatorChar))
                {
                    string? legacyDir = Path.GetDirectoryName(expanded);
                    if (!string.IsNullOrWhiteSpace(legacyDir))
                        settings.Directory = legacyDir;
                }
            }

            if (int.TryParse(legacyRetention, out int days))
                settings.RetentionDays = days;

            return Normalize(settings);
        }

        return new LoggingSettings();
    }

    public string ResolveDirectory(string baseDirectory)
    {
        string configured = string.IsNullOrWhiteSpace(Directory) ? DefaultDirectory : Directory.Trim();
        string expanded = Environment.ExpandEnvironmentVariables(configured);

        return Path.GetFullPath(
            Path.IsPathRooted(expanded)
                ? expanded
                : Path.Combine(baseDirectory, expanded));
    }

    private static LoggingSettings Normalize(LoggingSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Directory))
            settings.Directory = DefaultDirectory;

        if (settings.RetentionDays < 0)
            settings.RetentionDays = 0;

        return settings;
    }
}
