using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

public sealed class ExportExecutionResult
{
    public ExportSummary? Summary { get; set; }
    public bool NoDocumentsSelected { get; set; }
    public Exception? FatalError { get; set; }
}

public sealed class ExportProgressForm : Form
{
    private readonly string[] args;
    private readonly ExportConfig config;
    private readonly CancellationTokenSource cts = new();

    private readonly Label lblStatus;
    private readonly Label lblCurrentItem;
    private readonly ProgressBar progressBar;
    private readonly Label lblCounters;
    private readonly Button btnCancel;

    private bool isFinished;

    public ExportExecutionResult Result { get; } = new();

    public ExportProgressForm(string[] args, ExportConfig config)
    {
        this.args = args;
        this.config = config;

        Text = "Emails4KI";
        Icon = Program.LoadAppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(520, 178);
        Font = new Font("Segoe UI", 9.5f);

        lblStatus = new Label
        {
            Text = "Verbindung zum enaio Client wird hergestellt…",
            Location = new Point(16, 16),
            Size = new Size(488, 24),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };

        lblCurrentItem = new Label
        {
            Text = "Bitte warten…",
            Location = new Point(16, 44),
            Size = new Size(488, 22),
            ForeColor = SystemColors.GrayText,
            AutoEllipsis = true
        };

        progressBar = new ProgressBar
        {
            Location = new Point(16, 74),
            Size = new Size(488, 24),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30
        };

        lblCounters = new Label
        {
            Text = "Exportiert: 0   |   Übersprungen: 0   |   Fehler: 0",
            Location = new Point(16, 108),
            Size = new Size(340, 22)
        };

        btnCancel = new Button
        {
            Text = "Abbrechen",
            Location = new Point(384, 132),
            Size = new Size(120, 32)
        };
        btnCancel.Click += (_, _) => RequestCancellation();

        Controls.AddRange(new Control[]
        {
            lblStatus,
            lblCurrentItem,
            progressBar,
            lblCounters,
            btnCancel
        });

        Shown += (_, _) =>
        {
            Activate();
            StartStaExportThread();
        };

        FormClosing += (_, e) =>
        {
            if (!isFinished)
            {
                e.Cancel = true;
                RequestCancellation();
            }
        };
    }

    private void RequestCancellation()
    {
        if (cts.IsCancellationRequested || isFinished)
            return;

        cts.Cancel();
        btnCancel.Enabled = false;
        btnCancel.Text = "Wird abgebrochen…";
        lblStatus.Text = "Abbruch wird durchgeführt…";
    }

    private void StartStaExportThread()
    {
        // Dedizierter STA-Thread für enaio-COM-Automation:
        // Alle COM-Aufrufe laufen streng sequentiell auf einem einzigen STA-Thread (Handbuch S. 11),
        // während das WinForms-Fenster im UI-Thread reaktionsfähig bleibt.
        var worker = new Thread(RunExportOnStaThread)
        {
            IsBackground = true,
            Name = "EnaioExportStaWorker"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    private void RunExportOnStaThread()
    {
        string? tempFileToCleanup = null;

        try
        {
            using var enaio = new EnaioClient();
            string enaioUser = enaio.GetUserName();
            string enaioVersion = enaio.GetClientVersion();
            if (!string.IsNullOrWhiteSpace(enaioUser) || !string.IsNullOrWhiteSpace(enaioVersion))
                Logger.Log($"enaio-Verbindung: Benutzer='{enaioUser}', Client-Version='{enaioVersion}'");

            string? parameter = null;
            if (args.Length > 0)
            {
                parameter = args[0];
                Logger.Log("Parameter von enaio (%i) erhalten.");
                Logger.Log($"Argumente: {string.Join(", ", args)}");
            }
            else
            {
                Logger.Log("Keine Parameter übergeben – lese aktuelle Auswahl aus enaio.");
                try
                {
                    parameter = enaio.GetCurrentSelection();
                }
                catch (Exception ex)
                {
                    Logger.Log($"Auswahl konnte nicht aus enaio gelesen werden: {ex.Message}");
                }
            }

            var parseResult = ParameterParser.Parse(parameter);
            tempFileToCleanup = parseResult.TempFile;
            var docs = parseResult.Documents;

            Logger.Log($"Einträge empfangen: {docs.Count}" +
                       (parseResult.DuplicateEntriesSkipped > 0 ? $" (Duplikate entfernt: {parseResult.DuplicateEntriesSkipped})" : ""));

            if (docs.Count == 0)
            {
                Logger.Log("Keine Dokumente für den Export ausgewählt.");
                Result.NoDocumentsSelected = true;
                return;
            }

            var runner = new ExportRunner(enaio, config);
            Result.Summary = runner.Run(
                docs,
                onProgress: UpdateProgressSafe,
                cancellationToken: cts.Token);
        }
        catch (Exception ex)
        {
            Result.FatalError = ex;
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempFileToCleanup))
            {
                try
                {
                    if (File.Exists(tempFileToCleanup))
                    {
                        File.Delete(tempFileToCleanup);
                        Logger.Log($"Tempdatei gelöscht: {tempFileToCleanup}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Tempdatei konnte nicht gelöscht werden: {ex.Message}");
                }
            }

            FinishAndCloseSafe();
        }
    }

    private void UpdateProgressSafe(ExportProgressInfo info)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed)
                    return;

                if (progressBar.Style != ProgressBarStyle.Continuous)
                {
                    progressBar.Style = ProgressBarStyle.Continuous;
                    progressBar.Minimum = 0;
                    progressBar.Maximum = Math.Max(1, info.TotalCount);
                }

                progressBar.Value = Math.Min(progressBar.Maximum, info.CurrentIndex);
                lblStatus.Text = $"Dokument {info.CurrentIndex} von {info.TotalCount} wird verarbeitet…";
                lblCurrentItem.Text = info.CurrentItemLabel;
                lblCounters.Text =
                    $"Exportiert: {info.ExportedDocuments}   |   Übersprungen: {info.SkippedDocuments}   |   Fehler: {info.FailedDocuments}";
            }));
        }
        catch
        {
            // UI-Update-Fehler beim Schließen ignorieren
        }
    }

    private void FinishAndCloseSafe()
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        try
        {
            BeginInvoke(new Action(() =>
            {
                isFinished = true;
                Close();
            }));
        }
        catch
        {
            // Ignorieren
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            cts.Dispose();
        base.Dispose(disposing);
    }
}

