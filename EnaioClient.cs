using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class EnaioClient : IEnaioClient
{
    private const int ErrorNoFiles = -51;
    private const int AccessCheckSystemDeleteFlag = 8;
    private const int MainTypeFolder = 0;
    private const int MainTypeRegister = 99;

    private dynamic? app;

    public EnaioClient()
    {
        // Laut enaio client Programmierreferenz (S. 12) ist Late Binding über ProgID empfohlen,
        // da sich Objekt-GUIDs bei enaio-Updates ändern können.
        var comType = Type.GetTypeFromProgID("optimal_AS.Application")
            ?? throw new InvalidOperationException("enaio Client (optimal_AS.Application) ist auf diesem System nicht registriert.");

        app = Activator.CreateInstance(comType)
            ?? throw new InvalidOperationException("Verbindung zum enaio Client konnte nicht hergestellt werden.");
    }

    public string GetUserName()
    {
        try
        {
            // GetEnvironment(3) = osGetUserName (Handbuch S. 58)
            return Convert.ToString(app!.GetEnvironment((short)3)) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public string GetClientVersion()
    {
        try
        {
            // GetEnvironment(10) = FileVersion des Clients (Handbuch S. 58)
            return Convert.ToString(app!.GetEnvironment((short)10)) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public string GetLastError()
    {
        try
        {
            // GetLastError() liefert den Fehlertext des zuletzt aufgetretenen COM-Fehlers (Handbuch S. 63)
            return (Convert.ToString(app!.GetLastError()) ?? string.Empty).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    public EnaioObjectKind GetObjectKind(EnaioDocument doc)
    {
        int mainType = -1;

        try
        {
            // GetMainType(lObjectID, lObjectType): >= 0 Haupttyp, -22 unbekannter Typ, -26 unbekannte ID (Handbuch S. 64)
            mainType = Convert.ToInt32(app!.GetMainType(doc.Id, doc.Type));
        }
        catch
        {
            mainType = -1;
        }

        // Fallback auf High-Word des enaio-Objekttyps (0 = Ordner, 99 = Register, 1..8 = Dokumenttypen; Handbuch S. 12, 50, 57, 76)
        if (mainType < 0 && doc.Type >= 0)
            mainType = (int)(doc.Type >> 16);

        return mainType switch
        {
            MainTypeFolder => EnaioObjectKind.Folder,
            MainTypeRegister => EnaioObjectKind.Register,
            > 0 => EnaioObjectKind.Document,
            _ => EnaioObjectKind.Unknown
        };
    }

    public string? GetObjectTitle(EnaioDocument doc)
    {
        try
        {
            // GetObjectPattern(lIdent, lType, 1, Titel): liefert den konfigurierten Fenstertitel (Handbuch S. 73)
            object? titleObj = string.Empty;
            int ret = Convert.ToInt32(app!.GetObjectPattern(doc.Id, doc.Type, (short)1, ref titleObj));
            if (ret == 0)
            {
                string title = (Convert.ToString(titleObj) ?? string.Empty).Trim();
                return string.IsNullOrWhiteSpace(title) ? null : title;
            }
        }
        catch
        {
            // Titel ist rein informativ und darf den Export nicht abbrechen
        }

        return null;
    }

    public bool IsMarkedForSystemDelete(EnaioDocument doc)
    {
        try
        {
            // CheckObjectAccess(lObjectID, lObjectType, 8, 1, 0): 1 = zum Löschen vorgemerkt, 0 = nicht vorgemerkt (Handbuch S. 23)
            int ret = Convert.ToInt32(app!.CheckObjectAccess(doc.Id, doc.Type, AccessCheckSystemDeleteFlag, 1, (short)0));
            return ret == 1;
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Hinweis: System-Löschvormerkung konnte für Dokument {doc.Id} nicht geprüft werden: {ex.Message}");
            return false;
        }
    }

    public FileFetchResult GetFiles(EnaioDocument doc)
    {
        object? fileNames = null;

        // GetFilesFromIDEx(lObjectID, lObjectType, "|", true, ref fileNames) (Handbuch S. 61)
        // Rückgabewerte: 0 = OK, -51 = keine Dateien, -7 = Typ unbekannt, -15 = ID unbekannt
        int ret = Convert.ToInt32(app!.GetFilesFromIDEx(doc.Id, doc.Type, "|", true, ref fileNames));

        if (ret == ErrorNoFiles)
            return FileFetchResult.NoFiles();

        if (ret != 0)
        {
            string lastError = GetLastError();
            string reason = string.IsNullOrWhiteSpace(lastError)
                ? $"enaio GetFilesFromIDEx fehlgeschlagen (Code {ret})."
                : $"enaio GetFilesFromIDEx fehlgeschlagen (Code {ret}): {lastError}";
            return FileFetchResult.Fail(ret, reason);
        }

        string raw = Convert.ToString(fileNames) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return FileFetchResult.NoFiles();

        var files = new List<string>();
        foreach (var entry in raw.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = entry.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                files.Add(trimmed);
        }

        return files.Count == 0
            ? FileFetchResult.NoFiles()
            : FileFetchResult.Ok(files);
    }

    public Dictionary<string, string> GetMetadata(long id, long type)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // GetDataID(..., nMode: 10, bWriteToFile: 0): interne Feldnamen und -werte (Handbuch S. 54)
        // Im Fehlerfall wird ein leerer String zurückgegeben; Fehlertext über GetLastError().
        string? data = Convert.ToString(app!.GetDataID(id, type, (short)10, (short)0));
        if (string.IsNullOrWhiteSpace(data))
        {
            string lastError = GetLastError();
            string detail = string.IsNullOrWhiteSpace(lastError)
                ? "enaio GetDataID lieferte keine Metadaten zurück."
                : $"enaio GetDataID fehlgeschlagen: {lastError}";
            throw new InvalidOperationException(detail);
        }

        var lines = data.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var l in lines)
        {
            var p = l.Split('=', 2);
            if (p.Length == 2)
                meta[p[0].Trim()] = p[1].Trim();
        }

        return meta;
    }

    public EnaioDocument GetFolderFromDocument(EnaioDocument doc)
    {
        // GetObjectPath: SCHRANKINDEX,SCHRANKTYP;REGISTERINDEX1,REGISTERTYP;... (Handbuch S. 71)
        string? path = Convert.ToString(app!.GetObjectPath(doc.Id, doc.Type));
        if (string.IsNullOrWhiteSpace(path))
        {
            string lastError = GetLastError();
            string detail = string.IsNullOrWhiteSpace(lastError)
                ? $"Kein Ordnerpfad für Dokument {doc.Id} gefunden."
                : $"Kein Ordnerpfad für Dokument {doc.Id} gefunden: {lastError}";
            throw new InvalidOperationException(detail);
        }

        var first = path.Split(';', StringSplitOptions.RemoveEmptyEntries)[0];
        var p = first.Split(',');

        if (p.Length < 2 ||
            !long.TryParse(p[0].Trim(), out long folderId) ||
            !long.TryParse(p[1].Trim(), out long folderType))
        {
            throw new InvalidOperationException($"Ungültiger Ordnerpfad '{path}' für Dokument {doc.Id}.");
        }

        return new EnaioDocument
        {
            Id = folderId,
            Type = folderType
        };
    }

    public string GetCurrentSelection()
    {
        // Late Binding: ref-Parameter muss ein null-Objekt sein (string/int -> "Could not convert argument 0")
        object? result = null;
        int count = Convert.ToInt32(app!.GetCurrentSelection(ref result));

        if (count <= 0 || result == null)
            return string.Empty;

        return Convert.ToString(result) ?? string.Empty;
    }

    public void Dispose()
    {
        if (app != null && Marshal.IsComObject(app))
        {
            try
            {
                Marshal.ReleaseComObject(app);
            }
            catch
            {
                // Ignorieren beim Abbau der COM-Verbindung
            }
            finally
            {
                app = null;
            }
        }
    }
}
