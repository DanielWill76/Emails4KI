using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

public sealed class ExportSummaryForm : Form
{
    private readonly ExportSummary summary;
    private readonly string baseExportPath;
    private readonly string? logPath;
    private readonly CheckBox? chkOpenFolderOnClose;
    private bool folderAlreadyOpened;

    public bool ShouldOpenExportFoldersOnClose =>
        !folderAlreadyOpened && (chkOpenFolderOnClose?.Checked ?? false);

    public ExportSummaryForm(
        ExportSummary summary,
        string baseExportPath,
        string? logPath,
        bool defaultAutoOpenFolders)
    {
        this.summary = summary;
        this.baseExportPath = baseExportPath;
        this.logPath = logPath;

        bool hasIssues = summary.Issues.Count > 0;
        bool hasFolders = !string.IsNullOrEmpty(summary.RunFolder) && Directory.Exists(summary.RunFolder);

        Text = "Emails4KI – Ergebnis";
        Icon = Program.LoadAppIcon();
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = hasIssues ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog;
        MaximizeBox = hasIssues;
        MinimizeBox = false;
        MinimumSize = new Size(560, hasIssues ? 430 : 300);
        ClientSize = new Size(hasIssues ? 680 : 540, hasIssues ? 470 : 300);
        Font = new Font("Segoe UI", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = hasIssues ? 4 : 3
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (hasIssues)
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 1. Header mit Icon und Titel
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44
        };

        var icon = summary.FailedDocuments > 0 || summary.WasCancelled || summary.SkippedNonDocuments > 0
            ? SystemIcons.Warning
            : SystemIcons.Information;

        var picIcon = new PictureBox
        {
            Image = icon.ToBitmap(),
            SizeMode = PictureBoxSizeMode.AutoSize,
            Location = new Point(0, 4)
        };

        string headline = summary.WasCancelled
            ? "Export wurde abgebrochen"
            : summary.FailedDocuments > 0
                ? "Export mit Fehlern abgeschlossen"
                : summary.Issues.Count > 0
                    ? "Export mit Hinweisen abgeschlossen"
                    : "Export erfolgreich abgeschlossen";

        var lblHeadline = new Label
        {
            Text = headline,
            Location = new Point(44, 8),
            AutoSize = true,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold)
        };

        headerPanel.Controls.Add(picIcon);
        headerPanel.Controls.Add(lblHeadline);
        root.Controls.Add(headerPanel, 0, 0);

        // 2. Statistik-Tabelle
        var statsTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0, 6, 0, 10)
        };
        statsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 75));
        statsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        AddStatRow(statsTable, "Einträge verarbeitet:", summary.TotalDocuments.ToString());
        AddStatRow(
            statsTable,
            "E-Mails exportiert (TXT):",
            summary.ExportedFiles != summary.ExportedDocuments
                ? $"{summary.ExportedDocuments} ({summary.ExportedFiles} Dateien)"
                : summary.ExportedDocuments.ToString(),
            highlightBold: true);
        AddStatRow(statsTable, "Wegen Löschmarkierung übersprungen:", summary.TotalSkippedDelete.ToString());

        if (summary.SkippedNonDocuments > 0)
            AddStatRow(statsTable, "Ordner / Register übersprungen:", summary.SkippedNonDocuments.ToString(), Color.DarkOrange);

        AddStatRow(statsTable, "Einträge ohne Dateiinhalt:", summary.EmptyDocuments.ToString());

        if (summary.WasCleanedUp)
        {
            string cleanupDetail = summary.CleanedUpDirectories > 0
                ? $"{summary.CleanedUpFiles} Dateien ({summary.CleanedUpDirectories} leere Ordner)"
                : $"{summary.CleanedUpFiles} Dateien";
            AddStatRow(statsTable, "Nach Abbruch wieder entfernt:", cleanupDetail, Color.DarkSlateGray);
        }

        if (summary.FailedDocuments > 0)
            AddStatRow(statsTable, "Einträge mit Fehlern:", summary.FailedDocuments.ToString(), Color.Firebrick, highlightBold: true);
        if (summary.FilesMovedToErrorFolder > 0)
            AddStatRow(statsTable, "Originale im Ordner _fehler:", summary.FilesMovedToErrorFolder.ToString(), Color.Firebrick);

        root.Controls.Add(statsTable, 0, 1);

        // 3. Optionale Fehler- und Hinweisliste
        int footerRowIndex = 2;
        if (hasIssues)
        {
            var issuesPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 4, 0, 10)
            };

            var lblIssues = new Label
            {
                Text = $"Details zu Hinweisen und Fehlern ({summary.Issues.Count}):",
                Dock = DockStyle.Top,
                Height = 22,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };

            var listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true
            };
            listView.Columns.Add("Typ", 86);
            listView.Columns.Add("Dokument", 210);
            listView.Columns.Add("Meldung", 330);

            foreach (var issue in summary.Issues)
            {
                string typeLabel = issue.Kind switch
                {
                    ExportIssueKind.Error => "Fehler",
                    ExportIssueKind.NonDocument => "Hinweis",
                    _ => "Warnung"
                };

                var item = new ListViewItem(typeLabel);
                item.SubItems.Add(issue.DisplayName);
                item.SubItems.Add(issue.Message);

                if (issue.Kind == ExportIssueKind.Error)
                    item.ForeColor = Color.Firebrick;
                else if (issue.Kind == ExportIssueKind.NonDocument)
                    item.ForeColor = Color.DarkOrange;

                listView.Items.Add(item);
            }

            issuesPanel.Controls.Add(listView);
            issuesPanel.Controls.Add(lblIssues);
            root.Controls.Add(issuesPanel, 0, 2);
            footerRowIndex = 3;
        }

        // 4. Footer: Checkbox + Aktionsbuttons
        var footerPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2
        };
        footerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        if (hasFolders)
        {
            chkOpenFolderOnClose = new CheckBox
            {
                Text = "Exportordner beim Schließen automatisch im Explorer öffnen",
                AutoSize = true,
                Checked = defaultAutoOpenFolders,
                Margin = new Padding(0, 0, 0, 10)
            };
            footerPanel.Controls.Add(chkOpenFolderOnClose, 0, 0);
        }

        var buttonFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0)
        };

        var btnClose = new Button
        {
            Text = "Schließen",
            Size = new Size(110, 32),
            DialogResult = DialogResult.OK
        };

        var btnOpenLog = new Button
        {
            Text = "Protokoll öffnen",
            Size = new Size(135, 32),
            Enabled = !string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath)
        };
        btnOpenLog.Click += (_, _) => OpenLogFile();

        var btnOpenFolders = new Button
        {
            Text = "Exportordner öffnen",
            Size = new Size(155, 32),
            Enabled = hasFolders
        };
        btnOpenFolders.Click += (_, _) =>
        {
            folderAlreadyOpened = true;
            if (chkOpenFolderOnClose != null)
                chkOpenFolderOnClose.Checked = false;
            Program.OpenExportFolder(summary.RunFolder);
        };

        buttonFlow.Controls.Add(btnClose);
        buttonFlow.Controls.Add(btnOpenLog);
        buttonFlow.Controls.Add(btnOpenFolders);
        footerPanel.Controls.Add(buttonFlow, 0, hasFolders ? 1 : 0);

        root.Controls.Add(footerPanel, 0, footerRowIndex);
        Controls.Add(root);

        AcceptButton = btnClose;
        CancelButton = btnClose;

        // Sicherstellen, dass der Abschlussdialog zuverlässig im Vordergrund landet
        Shown += (_, _) =>
        {
            TopMost = true;
            Activate();
            TopMost = false;
        };
    }

    private static void AddStatRow(
        TableLayoutPanel table,
        string labelText,
        string valueText,
        Color? color = null,
        bool highlightBold = false)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var font = highlightBold
            ? new Font("Segoe UI", 9.5f, FontStyle.Bold)
            : new Font("Segoe UI", 9.5f);

        var lblKey = new Label
        {
            Text = labelText,
            AutoSize = true,
            Font = font,
            ForeColor = color ?? SystemColors.ControlText,
            Margin = new Padding(0, 2, 8, 2)
        };

        var lblVal = new Label
        {
            Text = valueText,
            AutoSize = true,
            Font = font,
            ForeColor = color ?? SystemColors.ControlText,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 2, 0, 2)
        };

        table.Controls.Add(lblKey, 0, row);
        table.Controls.Add(lblVal, 1, row);
    }

    private void OpenLogFile()
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = logPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Die Protokolldatei konnte nicht geöffnet werden:\n\n{ex.Message}",
                "Emails4KI",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}

