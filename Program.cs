using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        Logger.EnableConsoleOutput = false;

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception unhandledEx)
                Logger.LogUnhandledException("Unbehandelte Ausnahme (AppDomain)", unhandledEx);
        };

        Application.ThreadException += (_, eventArgs) =>
        {
            Logger.LogUnhandledException("Unbehandelte UI-Ausnahme (WinForms)", eventArgs.Exception);
        };

        try
        {
            var baseDir = AppContext.BaseDirectory;
            var configuration = ExportConfig.BuildConfiguration(baseDir);
            Logger.Init(configuration, baseDir);

            var config = ExportConfig.LoadFromConfiguration(configuration);

            string rawVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "1.0.0";
            string appVersion = rawVersion.Split('+')[0];

            Logger.Log($"Emails4KI v{appVersion} (GUI) – Export gestartet.");
            Logger.Log($"Konfiguration ({ExportConfig.ConfigFileName}): ExportPath='{config.ExportPath}', DeleteFlagField='{config.DeleteFlagField}', MaxFileNameLength={config.MaxFileNameLength}");
            Logger.Log("--------------------------------------------------");

            ExportExecutionResult executionResult;
            using (var progressForm = new ExportProgressForm(args, config))
            {
                Application.Run(progressForm);
                executionResult = progressForm.Result;
            }

            if (executionResult.FatalError != null)
                throw executionResult.FatalError;

            if (executionResult.NoDocumentsSelected || executionResult.Summary == null)
            {
                MessageBox.Show(
                    "Es wurden keine Dokumente im enaio Client ausgewählt.\n\nBitte markieren Sie zuerst die gewünschten Dokumente und starten Sie das Programm erneut.",
                    "Emails4KI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 0;
            }

            var summary = executionResult.Summary;
            LogSummary(summary);

            if (summary.WasCancelled && summary.CreatedFiles.Count > 0)
            {
                int newCount = summary.CreatedFiles.Count;
                string fileWord = newCount == 1 ? "neu erstellte Datei" : "neu erstellte Dateien";
                string existingHint = summary.ExportedFiles > newCount
                    ? "\n\nHinweis: Bereits zuvor vorhandene Dateien bleiben unberührt."
                    : "";

                var cleanupChoice = MessageBox.Show(
                    $"Der Export wurde abgebrochen.\n\nSollen die {newCount} in diesem Durchlauf {fileWord} (sowie dadurch leere Ordner) wieder entfernt werden?{existingHint}",
                    "Emails4KI – Aufräumen",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);

                if (cleanupChoice == DialogResult.Yes)
                    ExportRunner.CleanupCreatedArtifacts(summary);
            }

            bool shouldOpenFolders;
            using (var summaryForm = new ExportSummaryForm(
                summary,
                config.ExportPath,
                Logger.CurrentLogPath,
                defaultAutoOpenFolders: config.AutoOpenExportFolders && !summary.WasCancelled))
            {
                summaryForm.ShowDialog();
                shouldOpenFolders = summaryForm.ShouldOpenExportFoldersOnClose;
            }

            if (shouldOpenFolders)
                OpenExportFolder(summary.RunFolder);

            return summary.FailedDocuments > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Logger.EnsureFallbackInitialized(AppContext.BaseDirectory);
            Logger.LogCritical("Kritischer Fehler beim Export", ex);
            string logHint = !string.IsNullOrWhiteSpace(Logger.CurrentLogPath)
                ? $"\n\nProtokoll: {Logger.CurrentLogPath}"
                : "";

            MessageBox.Show(
                $"Der Export konnte nicht durchgeführt werden:\n\n{ex.Message}{logHint}",
                "Emails4KI – Fehler",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 2;
        }
        finally
        {
            Logger.Close();
        }
    }

    private static void LogSummary(ExportSummary summary)
    {
        Logger.Log(summary.WasCancelled ? "Export abgebrochen." : "Export abgeschlossen.");
        Logger.Log("--------------------------------------------------");
        Logger.Log($"Einträge für die Verarbeitung: {summary.TotalDocuments}");
        Logger.Log($"Dokumente exportiert: {summary.ExportedDocuments} (Dateien: {summary.ExportedFiles})");
        Logger.Log($"Übersprungen (Löschmarkierung Feld): {summary.SkippedDeleteFlag}");
        Logger.Log($"Übersprungen (enaio Löschvormerkung): {summary.SkippedSystemDelete}");
        if (summary.SkippedNonDocuments > 0)
            Logger.LogWarning($"Übersprungen (Ordner/Register): {summary.SkippedNonDocuments}");
        Logger.Log($"Dokumente ohne Inhalt: {summary.EmptyDocuments}");
        if (summary.FailedDocuments > 0)
            Logger.LogError($"Dokumente mit Fehlern: {summary.FailedDocuments}");
        else
            Logger.Log($"Dokumente mit Fehlern: {summary.FailedDocuments}");
        Logger.Log("--------------------------------------------------");
    }

    internal static System.Drawing.Icon? LoadAppIcon()
    {
        try
        {
            return System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            return null;
        }
    }

    internal static void OpenExportFolder(string folder)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{folder}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Explorer konnte nicht geöffnet werden: {ex.Message}");
        }
    }
}

