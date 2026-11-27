using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

public enum ExportIssueKind
{
    Error,
    Warning,
    NonDocument
}

public class ExportIssue
{
    public long DocumentId { get; init; }
    public string DisplayName { get; init; } = "";
    public ExportIssueKind Kind { get; init; }
    public string Message { get; init; } = "";
}

public class ExportProgressInfo
{
    public int CurrentIndex { get; init; }
    public int TotalCount { get; init; }
    public string CurrentItemLabel { get; init; } = "";
    public int ExportedDocuments { get; init; }
    public int SkippedDocuments { get; init; }
    public int FailedDocuments { get; init; }
}

public class ExportSummary
{
    public int TotalDocuments { get; set; }
    public int ExportedDocuments { get; set; }
    public int ExportedFiles { get; set; }
    public int SkippedDeleteFlag { get; set; }
    public int SkippedSystemDelete { get; set; }
    public int SkippedNonDocuments { get; set; }

    public int EmptyDocuments { get; set; }
    public int FailedDocuments { get; set; }
    public bool WasCancelled { get; set; }
    public bool WasCleanedUp { get; set; }
    public int CleanedUpFiles { get; set; }
    public int CleanedUpDirectories { get; set; }
    public string RunFolder { get; set; } = "";
    public string ErrorFolder { get; set; } = "";
    public int FilesMovedToErrorFolder { get; set; }
    public List<string> CreatedFiles { get; } = new();
    public List<string> CreatedDirectories { get; } = new();
    public List<ExportIssue> Issues { get; } = new();

    public int TotalSkippedDelete => SkippedDeleteFlag + SkippedSystemDelete;
    public bool HasWarningsOrErrors => FailedDocuments > 0 || SkippedNonDocuments > 0 || Issues.Count > 0;
}

public class ExportRunner
{
    private static readonly string[] MailExtensions = { ".eml", ".msg" };

    private readonly IEnaioClient enaio;
    private readonly ExportConfig config;

    public ExportRunner(IEnaioClient enaio, ExportConfig config)
    {
        this.enaio = enaio;
        this.config = config;
    }

    public ExportSummary Run(
        IReadOnlyList<EnaioDocument> docs,
        Action<ExportProgressInfo>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var summary = new ExportSummary { TotalDocuments = docs.Count };

        string runFolder = Path.Combine(config.ExportPath, DateTime.Now.ToString(config.RunFolderPattern));
        runFolder = ResolveUniqueDirectory(runFolder);
        string workFolder = Path.Combine(runFolder, "_work");
        string errorFolder = Path.Combine(runFolder, "_fehler");

        summary.RunFolder = runFolder;
        summary.ErrorFolder = errorFolder;
        EnsureDirectoryTracked(workFolder, summary);
        Logger.Log($"Laufordner: {runFolder}");

        int mailNumber = 0;

        try
        {
            for (int index = 0; index < docs.Count; index++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    summary.WasCancelled = true;
                    Logger.Log("Export durch Benutzer abgebrochen.");
                    break;
                }

                var doc = docs[index];
                string? title = enaio.GetObjectTitle(doc);
                string displayName = string.IsNullOrWhiteSpace(title)
                    ? $"Dokument {doc.Id}"
                    : $"{title} (ID {doc.Id})";
                string uiLabel = !string.IsNullOrWhiteSpace(title) ? title : "Mail wird geladen…";
                int current = index + 1;

                ReportProgress(onProgress, current, docs.Count, uiLabel, summary);

                try
                {
                    ProcessDocument(doc, displayName, workFolder, runFolder, errorFolder, ref mailNumber, summary,
                        label => ReportProgress(onProgress, current, docs.Count, label, summary));
                }
                catch (Exception ex)
                {
                    summary.FailedDocuments++;
                    Logger.LogError($"Fehler bei {displayName}: {ex.Message}", ex);
                    AddIssue(summary, doc, displayName, ExportIssueKind.Error, ex.Message);
                }

                ReportProgress(onProgress, current, docs.Count, uiLabel, summary);
            }
        }
        finally
        {
            RemoveWorkFolder(workFolder);
        }

        return summary;
    }

    private void ProcessDocument(
        EnaioDocument doc,
        string displayName,
        string workFolder,
        string runFolder,
        string errorFolder,
        ref int mailNumber,
        ExportSummary summary,
        Action<string> reportLabel)
    {
        // Ordner/Register enthalten keine Mail – klare Meldung statt Fehler von GetFiles
        var kind = enaio.GetObjectKind(doc);
        if (kind == EnaioObjectKind.Folder || kind == EnaioObjectKind.Register)
        {
            string kindText = kind == EnaioObjectKind.Folder ? "Ordner" : "Register";
            string msg = $"{kindText} kann nicht direkt exportiert werden – bitte die enthaltenen E-Mails markieren.";
            Logger.LogWarning($"{displayName}: {msg}");
            summary.SkippedNonDocuments++;
            AddIssue(summary, doc, displayName, ExportIssueKind.NonDocument, msg);
            return;
        }

        var docMeta = enaio.GetMetadata(doc.Id, doc.Type);

        string deleteFlagField = config.GetDeleteFlagField(doc.Type);
        if (docMeta.TryGetValue(deleteFlagField, out string? deleteFlag) && IsTruthyDeleteFlag(deleteFlag))
        {
            Logger.Log($"{displayName} wird übersprungen (Löschmarkierung {deleteFlagField}).");
            summary.SkippedDeleteFlag++;
            return;
        }

        if (config.CheckSystemDeleteFlag && enaio.IsMarkedForSystemDelete(doc))
        {
            Logger.Log($"{displayName} wird übersprungen (in enaio zum Löschen vorgemerkt).");
            summary.SkippedSystemDelete++;
            return;
        }

        var fileResult = enaio.GetFiles(doc);
        if (fileResult.Status == FileFetchStatus.Empty)
        {
            Logger.Log($"{displayName} hat keinen Dateiinhalt.");
            summary.EmptyDocuments++;
            return;
        }

        if (fileResult.Status == FileFetchStatus.Error)
        {
            string err = fileResult.ErrorMessage ?? $"Datei konnte nicht geladen werden (Code {fileResult.ErrorCode}).";
            Logger.LogError($"Fehler bei {displayName}: {err}");
            summary.FailedDocuments++;
            AddIssue(summary, doc, displayName, ExportIssueKind.Error, err);
            return;
        }

        int converted = 0;
        bool failed = false;

        foreach (string sourceFile in fileResult.Files)
        {
            string ext = Path.GetExtension(sourceFile);
            if (!MailExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                string msg = $"Datei '{Path.GetFileName(sourceFile)}' ist keine E-Mail (.eml/.msg) und wurde übersprungen.";
                Logger.LogWarning($"{displayName}: {msg}");
                AddIssue(summary, doc, displayName, ExportIssueKind.Warning, msg);
                continue;
            }

            mailNumber++;
            string workCopy = Path.Combine(workFolder, $"{mailNumber:D4}{ext.ToLowerInvariant()}");

            try
            {
                FileNameBuilder.CopyFileSafe(sourceFile, workCopy);

                var mail = MailToTextConverter.Read(workCopy);
                string txtName = FileNameBuilder.BuildMailName(mailNumber, mail.Date, mail.Subject, config.MaxFileNameLength);
                reportLabel(txtName);

                string target = FileNameBuilder.ResolveCollision(Path.Combine(runFolder, txtName), config.MaxFileNameLength);
                File.WriteAllText(target, MailToTextConverter.Format(mail), new System.Text.UTF8Encoding(config.Utf8Bom));
                summary.CreatedFiles.Add(target);

                FileNameBuilder.DeleteFileSafe(workCopy);
                converted++;
                summary.ExportedFiles++;
                Logger.Log($"Exportiert: {target}");
            }
            catch (Exception ex)
            {
                failed = true;
                Logger.LogError($"Konvertierung fehlgeschlagen bei {displayName}: {ex.Message}", ex);
                AddIssue(summary, doc, displayName, ExportIssueKind.Error, $"Konvertierung fehlgeschlagen: {ex.Message}");
                MoveToErrorFolder(workCopy, errorFolder, summary);
            }
        }

        if (failed)
            summary.FailedDocuments++;
        else if (converted > 0)
            summary.ExportedDocuments++;
        else
            summary.EmptyDocuments++;
    }

    private static void MoveToErrorFolder(string workCopy, string errorFolder, ExportSummary summary)
    {
        try
        {
            if (!File.Exists(workCopy))
                return;

            EnsureDirectoryTracked(errorFolder, summary);
            string target = FileNameBuilder.ResolveCollision(Path.Combine(errorFolder, Path.GetFileName(workCopy)));
            File.Move(workCopy, target);
            summary.FilesMovedToErrorFolder++;
            Logger.Log($"Original in Fehlerordner verschoben: {target}");
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Original konnte nicht in den Fehlerordner verschoben werden: {ex.Message}");
        }
    }

    private static void RemoveWorkFolder(string workFolder)
    {
        try
        {
            if (Directory.Exists(workFolder))
                Directory.Delete(workFolder, recursive: true);
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Arbeitsordner konnte nicht entfernt werden: {ex.Message}");
        }
    }

    private static string ResolveUniqueDirectory(string path)
    {
        if (!Directory.Exists(path))
            return path;

        int i = 1;
        while (Directory.Exists($"{path}_{i}"))
            i++;
        return $"{path}_{i}";
    }

    private static void AddIssue(ExportSummary summary, EnaioDocument doc, string displayName, ExportIssueKind kind, string message)
    {
        summary.Issues.Add(new ExportIssue
        {
            DocumentId = doc.Id,
            DisplayName = displayName,
            Kind = kind,
            Message = message
        });
    }

    private static void ReportProgress(
        Action<ExportProgressInfo>? onProgress,
        int currentIndex,
        int totalCount,
        string itemLabel,
        ExportSummary summary)
    {
        onProgress?.Invoke(new ExportProgressInfo
        {
            CurrentIndex = currentIndex,
            TotalCount = totalCount,
            CurrentItemLabel = itemLabel,
            ExportedDocuments = summary.ExportedDocuments,
            SkippedDocuments = summary.TotalSkippedDelete + summary.SkippedNonDocuments,
            FailedDocuments = summary.FailedDocuments
        });
    }
    /// <summary>
    /// Entfernt nach einem Abbruch ausschließlich die in diesem Durchlauf neu erstellten Dateien
    /// sowie anschließend leer gewordene, neu angelegte Verzeichnisse.
    /// Bereits zuvor vorhandene (ggf. überschriebene) Dateien und Bestandsordner bleiben unberührt.
    /// </summary>
    public static void CleanupCreatedArtifacts(ExportSummary summary)
    {
        int deletedFiles = 0;
        foreach (string file in summary.CreatedFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);

                    File.Delete(file);
                    deletedFiles++;
                    Logger.Log($"Aufgeräumt (Datei entfernt): {file}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Aufräumen fehlgeschlagen für Datei '{file}': {ex.Message}");
            }
        }

        int deletedDirs = 0;
        var dirsDeepestFirst = new List<string>(summary.CreatedDirectories);
        dirsDeepestFirst.Sort((a, b) => b.Length.CompareTo(a.Length));

        foreach (string dir in dirsDeepestFirst)
        {
            try
            {
                if (Directory.Exists(dir) && IsDirectoryEmpty(dir))
                {
                    Directory.Delete(dir, recursive: false);
                    deletedDirs++;
                    Logger.Log($"Aufgeräumt (leerer Ordner entfernt): {dir}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Aufräumen fehlgeschlagen für Ordner '{dir}': {ex.Message}");
            }
        }

        summary.CleanedUpFiles = deletedFiles;
        summary.CleanedUpDirectories = deletedDirs;
        summary.WasCleanedUp = true;

        Logger.Log($"Aufräumen abgeschlossen: {deletedFiles} neu erstellte Datei(en) und {deletedDirs} leere(r) Ordner entfernt.");
    }

    private static void EnsureDirectoryTracked(string path, ExportSummary summary)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Directory.Exists(fullPath))
            return;

        var missingSegments = new Stack<string>();
        string? current = fullPath;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            missingSegments.Push(current);
            var parent = Directory.GetParent(current);
            if (parent == null)
                break;
            current = parent.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        while (missingSegments.Count > 0)
        {
            string dirToCreate = missingSegments.Pop();
            Directory.CreateDirectory(dirToCreate);
            if (!summary.CreatedDirectories.Contains(dirToCreate, StringComparer.OrdinalIgnoreCase))
                summary.CreatedDirectories.Add(dirToCreate);
        }
    }

    private static bool IsDirectoryEmpty(string path)
    {
        using var enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return !enumerator.MoveNext();
    }

    private static bool IsTruthyDeleteFlag(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return false;

        string value = rawValue.Trim().ToLowerInvariant();
        return value == "1" || value == "ja" || value == "true";
    }
}


