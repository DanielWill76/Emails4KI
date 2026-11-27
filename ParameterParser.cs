using System;
using System.Collections.Generic;
using System.IO;

public class ParseResult
{
    public List<EnaioDocument> Documents { get; set; } = new();
    public string? TempFile { get; set; }
    public int DuplicateEntriesSkipped { get; set; }
    public int InvalidEntriesSkipped { get; set; }
}

public static class ParameterParser
{
    public static ParseResult Parse(string? param)
    {
        var result = new ParseResult();

        if (string.IsNullOrWhiteSpace(param))
            return result;

        var seen = new HashSet<(long Id, long Type)>();

        try
        {
            // enaio %i Übergabe (Pfad zu einer temporären Datei)
            if (File.Exists(param))
            {
                result.TempFile = param;

                var lines = File.ReadAllLines(param);
                foreach (var line in lines)
                    AddEntry(line, seen, result);

                return result;
            }

            // Direkte Übergabe / Debug-Variante (z. B. "12345,131072;12346,131072")
            var entries = param.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in entries)
                AddEntry(entry, seen, result);

            return result;
        }
        catch (Exception ex)
        {
            Logger.Log($"Fehler beim Lesen der Übergabeparameter: {ex.Message}");
            return result;
        }
    }

    private static void AddEntry(string? rawEntry, HashSet<(long Id, long Type)> seen, ParseResult result)
    {
        if (string.IsNullOrWhiteSpace(rawEntry))
            return;

        if (!TryParseDocumentEntry(rawEntry, out var doc))
        {
            result.InvalidEntriesSkipped++;
            return;
        }

        if (!seen.Add((doc.Id, doc.Type)))
        {
            result.DuplicateEntriesSkipped++;
            Logger.Log($"Doppelter Eintrag übersprungen: ID={doc.Id}, Typ={doc.Type}");
            return;
        }

        result.Documents.Add(doc);
    }

    private static bool TryParseDocumentEntry(string? rawEntry, out EnaioDocument document)
    {
        document = new EnaioDocument();

        if (string.IsNullOrWhiteSpace(rawEntry))
            return false;

        var parts = rawEntry.Split(',');
        if (parts.Length < 2)
        {
            Logger.Log($"Ungültiger Dokumenteintrag übersprungen: '{rawEntry}'");
            return false;
        }

        if (!long.TryParse(parts[0].Trim(), out long id) ||
            !long.TryParse(parts[1].Trim(), out long type))
        {
            Logger.Log($"Ungültiger Dokumenteintrag übersprungen: '{rawEntry}'");
            return false;
        }

        document.Id = id;
        document.Type = type;
        return true;
    }
}
