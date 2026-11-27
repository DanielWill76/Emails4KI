using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class FileNameBuilder
{
    // SharePoint / OneDrive / Microsoft Teams verbieten zusätzlich zu Windows-Zeichen
    // u. a. #, %, &, {, }, ~, + sowie führende/abschließende Punkte und Tilden.
    private static readonly HashSet<char> InvalidChars = BuildInvalidCharSet();

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static HashSet<char> BuildInvalidCharSet()
    {
        var set = new HashSet<char>(Path.GetInvalidFileNameChars());
        foreach (char c in new[] { '#', '%', '&', '{', '}', '~', '+', '*', '?', '<', '>', '|', ':', '"', '/', '\\' })
            set.Add(c);
        return set;
    }

    public static string BuildMailName(int number, DateTimeOffset? date, string? subject, int maxLength, string extension = ".txt")
    {
        string cleanExt = SanitizeExtension(extension);
        string prefix = $"{number:D4}_{(date.HasValue ? date.Value.ToString("yyyyMMdd") : "ohne-Datum")}_";
        string subj = Sanitize(subject, "ohne-Betreff");

        int effectiveMax = maxLength >= 20 ? maxLength : 140;
        int maxSubject = Math.Max(1, effectiveMax - prefix.Length - cleanExt.Length);
        if (subj.Length > maxSubject)
        {
            subj = subj[..maxSubject].TrimEnd('.', ' ', '_', '~');
            if (string.IsNullOrWhiteSpace(subj))
                subj = "ohne-Betreff";
        }

        return EscapeReservedWindowsName($"{prefix}{subj}") + cleanExt;
    }
    public static string Sanitize(string? name, string fallback = "Dokument")
    {
        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsControl(c) || InvalidChars.Contains(c))
                sb.Append('_');
            else
                sb.Append(c);
        }

        string result = sb.ToString();

        // Doppelte Punkte reduzieren
        while (result.Contains(".."))
            result = result.Replace("..", ".");

        // Mehrfache Leerzeichen und Unterstriche reduzieren
        while (result.Contains("  "))
            result = result.Replace("  ", " ");

        result = result.Replace(" _ ", "_").Replace(" _", "_").Replace("_ ", "_");

        while (result.Contains("__"))
            result = result.Replace("__", "_");

        // Führende und abschließende Punkte, Leerzeichen und Tilden entfernen (Windows & SharePoint/Teams)
        result = result.Trim(' ', '.', '~');

        if (string.IsNullOrWhiteSpace(result))
            return fallback;

        return EscapeReservedWindowsName(result);
    }

    public static string ResolveCollision(string path, int maxFileNameLength = 140)
    {
        if (!File.Exists(path))
            return path;

        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        int effectiveMax = maxFileNameLength >= 20 ? maxFileNameLength : 140;

        int i = 1;
        while (true)
        {
            string suffix = $"_{i}";
            int allowedBase = Math.Max(1, effectiveMax - suffix.Length - ext.Length);
            string baseName = name.Length > allowedBase
                ? name[..allowedBase].TrimEnd('.', ' ', '_')
                : name;

            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "Dokument";

            string candidate = Path.Combine(dir, $"{baseName}{suffix}{ext}");

            if (!File.Exists(candidate))
                return candidate;

            i++;
        }
    }

    public static void CopyFileSafe(string sourceFile, string targetFile)
    {
        if (!File.Exists(sourceFile))
            throw new FileNotFoundException($"Quelldatei aus enaio wurde nicht gefunden: {sourceFile}", sourceFile);

        File.Copy(sourceFile, targetFile, true);

        // enaio GetFilesFromIDEx(..., bWriteProtected: true) markiert Quelldateien als ReadOnly;
        // File.Copy übernimmt dieses Attribut – sonst lässt sich die Arbeitskopie nicht löschen.
        ClearReadOnlyAttribute(targetFile);
    }

    public static void DeleteFileSafe(string path)
    {
        if (!File.Exists(path))
            return;

        ClearReadOnlyAttribute(path);
        File.Delete(path);
    }
    private static void ClearReadOnlyAttribute(string filePath)
    {
        try
        {
            var attrs = File.GetAttributes(filePath);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(filePath, attrs & ~FileAttributes.ReadOnly);
        }
        catch
        {
            // Nicht-kritisch, falls das Dateisystem keine Attribute unterstützt
        }
    }

    private static string SanitizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;

        string ext = extension.Trim();
        if (!ext.StartsWith('.'))
            ext = "." + ext;

        var sb = new StringBuilder(ext.Length);
        sb.Append('.');
        for (int i = 1; i < ext.Length; i++)
        {
            char c = ext[i];
            if (!char.IsControl(c) && !InvalidChars.Contains(c) && c != ' ')
                sb.Append(c);
        }

        return sb.Length > 1 ? sb.ToString() : string.Empty;
    }

    private static string EscapeReservedWindowsName(string name)
    {
        string checkName = name;
        int dotIdx = name.IndexOf('.');
        if (dotIdx > 0)
            checkName = name[..dotIdx];

        if (ReservedWindowsNames.Contains(checkName))
            return name + "_";

        return name;
    }
}

