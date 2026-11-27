using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Configuration;

internal static class TestProgram
{
    private static int passed;
    private static int failed;

    static int Main()
    {
        Logger.EnableConsoleOutput = false;

        Run("FileNameBuilder: Teams-/SharePoint-Zeichen & doppelte Trennzeichen", TestSanitizeTeamsChars);
        Run("FileNameBuilder: Reservierte Windows-Namen entschärfen", TestReservedWindowsNames);
        Run("FileNameBuilder: Mail-Dateiname mit Nummer, Datum, Betreff und Längenbegrenzung", TestBuildMailName);
        Run("FileNameBuilder: ResolveCollision mit Längenbegrenzung", TestResolveCollision);
        Run("FileNameBuilder: CopyFileSafe/DeleteFileSafe entfernen ReadOnly", TestCopyFileSafeReadOnly);

        Run("ExportConfig: Umgebungsvariablen, Kommentare, Defaults & Löschfeld je Typ", TestExportConfigParse);
        Run("ExportConfig: Ungültiges RunFolderPattern wirft Fehler", TestExportConfigInvalidPattern);
        Run("ExportConfig: Laufordnername behält festen Text (Regression Expor_)", TestRunFolderName);
        Run("LoggingSettings: Defaults und Konfigurationsbindung", TestLoggingSettings);

        Run("ParameterParser: Datei-Übergabe, Deduplizierung und ungültige Zeilen", TestParameterParser);

        Run("MailToTextConverter: EML mit Umlauten (ISO-8859-1) als UTF-8", TestConverterEmlUmlauts);
        Run("MailToTextConverter: EML nur HTML wird zu Text", TestConverterHtmlOnly);
        Run("ExportRunner: Export, Laufordner, Arbeitsordner entfernt", TestExportRunnerSuccess);
        Run("ExportRunner: Ältere enaio-Mails (.ima, RFC 822) werden exportiert", TestExportRunnerImaMail);
        Run("ExportRunner: Löschflag je Archiv/Typ und System-Löschvormerkung", TestExportRunnerDeleteFlags);
        Run("ExportRunner: Markierte Ordner und Register erkennen & melden", TestExportRunnerNonDocuments);
        Run("ExportRunner: GetFiles Empty vs. Error", TestExportRunnerFileErrors);
        Run("ExportRunner: Defekte Mail landet im Fehlerordner", TestExportRunnerCorruptMail);
        Run("ExportRunner: Abbruch über CancellationToken", TestExportRunnerCancellation);

        Console.WriteLine();
        Console.WriteLine($"Testergebnis: {passed} bestanden, {failed} fehlgeschlagen.");
        return failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            passed++;
            Console.WriteLine($"[OK]   {name}");
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"[FAIL] {name}: {ex.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string? context = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{(context != null ? context + ": " : "")}Erwartet '{expected}', erhalten '{actual}'.");
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "Emails4KI_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { }
    }

    private static void TestSanitizeTeamsChars()
    {
        string sanitized = FileNameBuilder.Sanitize("..~Projekt #1 % 100 & {Test} + A/B:C..~");
        Assert(!sanitized.Contains('#'), "Enthält #");
        Assert(!sanitized.Contains('%'), "Enthält %");
        Assert(!sanitized.Contains('&'), "Enthält &");
        Assert(!sanitized.Contains('/'), "Enthält /");
        Assert(!sanitized.Contains(':'), "Enthält :");
        Assert(!sanitized.Contains("__"), "Doppelte Unterstriche");
        Assert(!sanitized.StartsWith('.') && !sanitized.EndsWith('.') && !sanitized.StartsWith('~'), "Führende/abschließende Sonderzeichen");
    }

    private static void TestReservedWindowsNames()
    {
        AssertEqual("CON_", FileNameBuilder.Sanitize("CON"));
        AssertEqual("nul_", FileNameBuilder.Sanitize("nul"));
        AssertEqual("COM1_", FileNameBuilder.Sanitize("COM1"));
        AssertEqual("LPT9.txt_", FileNameBuilder.Sanitize("LPT9.txt"));
    }

    private static void TestBuildMailName()
    {
        var date = new DateTimeOffset(2025, 3, 7, 10, 0, 0, TimeSpan.Zero);
        AssertEqual("0001_20250307_Antrag_Änderung.txt", FileNameBuilder.BuildMailName(1, date, "Antrag: Änderung", 140));
        AssertEqual("0002_ohne-Datum_ohne-Betreff.txt", FileNameBuilder.BuildMailName(2, null, "  ", 140));

        string longName = FileNameBuilder.BuildMailName(3, date, new string('A', 200), 40);
        Assert(longName.Length <= 40, $"Länge {longName.Length} überschreitet 40: {longName}");
        Assert(longName.EndsWith(".txt"), "Endung fehlt");
    }

    private static void TestResolveCollision()
    {
        string dir = CreateTempDir();
        try
        {
            string baseFile = Path.Combine(dir, "Test.txt");
            File.WriteAllText(baseFile, "a");
            string c1 = FileNameBuilder.ResolveCollision(baseFile, 30);
            AssertEqual(Path.Combine(dir, "Test_1.txt"), c1);

            File.WriteAllText(c1, "b");
            string c2 = FileNameBuilder.ResolveCollision(baseFile, 30);
            AssertEqual(Path.Combine(dir, "Test_2.txt"), c2);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    private static void TestCopyFileSafeReadOnly()
    {
        string dir = CreateTempDir();
        try
        {
            string src = Path.Combine(dir, "src.eml");
            string dst = Path.Combine(dir, "dst.eml");
            File.WriteAllText(src, "version 1");
            File.SetAttributes(src, FileAttributes.ReadOnly);

            FileNameBuilder.CopyFileSafe(src, dst);
            Assert((File.GetAttributes(dst) & FileAttributes.ReadOnly) == 0, "Zieldatei darf nicht ReadOnly sein");

            FileNameBuilder.DeleteFileSafe(dst);
            Assert(!File.Exists(dst), "Datei wurde nicht gelöscht");
            File.SetAttributes(src, FileAttributes.Normal);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    private static void TestExportConfigParse()
    {
        string json = """
        {
          // Kommentar erlaubt
          "exportPath": "%TEMP%\\Emails4KITest",
          "deleteFlagFieldByType": { "65537": "zahl2", },
        }
        """;

        var cfg = ExportConfig.Parse(json);
        Assert(!cfg.ExportPath.Contains("%TEMP%"), "Umgebungsvariable wurde nicht aufgelöst");
        AssertEqual(140, cfg.MaxFileNameLength, "Default MaxFileNameLength");
        AssertEqual(true, cfg.CheckSystemDeleteFlag, "Default CheckSystemDeleteFlag");
        AssertEqual(true, cfg.Utf8Bom, "Default Utf8Bom");
        AssertEqual("zahl1", cfg.GetDeleteFlagField(1), "Default Löschfeld");
        AssertEqual("zahl2", cfg.GetDeleteFlagField(65537), "Löschfeld je Typ");
    }

    private static void TestRunFolderName()
    {
        var now = new DateTime(2026, 11, 27, 12, 55, 17);
        AssertEqual("Export_20261127_125517", new ExportConfig().FormatRunFolderName(now));
        var cfg = ExportConfig.Parse("{ \"exportPath\": \"C:\\\\Temp\" }");
        AssertEqual("Export_20261127_125517", cfg.FormatRunFolderName(now));
    }

    private static void TestExportConfigInvalidPattern()
    {
        bool threw = false;
        try
        {
            ExportConfig.Parse("{ \"exportPath\": \"C:\\\\Temp\", \"runFolderPattern\": \"yyyy'\" }");
        }
        catch (FormatException)
        {
            threw = true;
        }
        Assert(threw, "Ungültiges RunFolderPattern wurde nicht abgelehnt");
    }

    private static void TestLoggingSettings()
    {
        var empty = new ConfigurationBuilder().Build();
        var defaults = LoggingSettings.BindFrom(empty);
        AssertEqual(LoggingSettings.DefaultRetentionDays, defaults.RetentionDays, "Default RetentionDays");

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileLogging:Directory"] = @"C:\Temp\Logs",
                ["FileLogging:RetentionDays"] = "7"
            })
            .Build();
        var bound = LoggingSettings.BindFrom(cfg);
        AssertEqual(7, bound.RetentionDays, "RetentionDays");
        AssertEqual(@"C:\Temp\Logs", bound.Directory, "Directory");
    }

    private static void TestParameterParser()
    {
        string dir = CreateTempDir();
        try
        {
            string file = Path.Combine(dir, "param.txt");
            File.WriteAllLines(file, new[] { "100,65537", "101,65537", "100,65537", "kaputt", "abc,1" });

            var result = ParameterParser.Parse(file);
            AssertEqual(2, result.Documents.Count, "Dokumente");
            AssertEqual(1, result.DuplicateEntriesSkipped, "Duplikate");
            AssertEqual(2, result.InvalidEntriesSkipped, "Ungültige Einträge");
            AssertEqual(file, result.TempFile);

            var direct = ParameterParser.Parse("1,2;3,4");
            AssertEqual(2, direct.Documents.Count, "Direkte Übergabe");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Quoted-Printable mit ISO-8859-1: Umlaute müssen als echtes UTF-8 in der TXT landen
    private const string EmlUmlauts =
        "From: =?iso-8859-1?Q?J=FCrgen_M=FCller?= <j.mueller@example.org>\r\n" +
        "To: Anna <anna@example.org>\r\n" +
        "Cc: Bob <bob@example.org>\r\n" +
        "Date: Fri, 07 Mar 2025 10:15:00 +0100\r\n" +
        "Subject: =?iso-8859-1?Q?Gr=FC=DFe_aus_K=F6ln?=\r\n" +
        "MIME-Version: 1.0\r\n" +
        "Content-Type: text/plain; charset=iso-8859-1\r\n" +
        "Content-Transfer-Encoding: quoted-printable\r\n\r\n" +
        "Sehr geehrte Damen und Herren,\r\n\r\n=E4=F6=FC =DF - sch=F6ne Gr=FC=DFe\r\n";

    private static string WriteEml(string dir, string name, string content)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(content));
        return path;
    }

    private static void TestConverterEmlUmlauts()
    {
        string dir = CreateTempDir();
        try
        {
            string eml = WriteEml(dir, "a.eml", EmlUmlauts);
            string txt = Path.Combine(dir, "a.txt");
            MailToTextConverter.WriteText(eml, txt, utf8Bom: true);

            byte[] bytes = File.ReadAllBytes(txt);
            Assert(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF-8-BOM fehlt");

            string text = File.ReadAllText(txt, Encoding.UTF8);
            Assert(text.Contains("Betreff: Grüße aus Köln"), "Betreff mit Umlauten fehlt");
            Assert(text.Contains("Von: Jürgen Müller <j.mueller@example.org>"), "Absender fehlt");
            Assert(text.Contains("Cc: Bob <bob@example.org>"), "Cc fehlt");
            Assert(text.Contains("Datum: 07.03.2025 10:15"), "Datum fehlt");
            Assert(text.Contains("äöü ß - schöne Grüße"), "Textkörper mit Umlauten fehlt");

            MailToTextConverter.WriteText(eml, txt, utf8Bom: false);
            Assert(File.ReadAllBytes(txt)[0] != 0xEF, "BOM trotz Utf8Bom=false");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    private static void TestConverterHtmlOnly()
    {
        string dir = CreateTempDir();
        try
        {
            string eml = WriteEml(dir, "h.eml",
                "From: a@example.org\r\nTo: b@example.org\r\nSubject: Test\r\nDate: Fri, 07 Mar 2025 10:15:00 +0100\r\n" +
                "MIME-Version: 1.0\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<html><body><p>Hallo <b>Welt</b></p></body></html>");
            var mail = MailToTextConverter.Read(eml);
            Assert(mail.Body.Contains("Hallo Welt"), $"HTML nicht umgewandelt: '{mail.Body}'");
            Assert(!mail.Body.Contains('<'), "HTML-Tags im Text");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    private static void TestExportRunnerSuccess()
    {
        string dir = CreateTempDir();
        try
        {
            string exportDir = Path.Combine(dir, "export");
            string src = WriteEml(dir, "cache_1.eml", EmlUmlauts);
            File.SetAttributes(src, FileAttributes.ReadOnly);

            var fake = new FakeEnaioClient();
            fake.AddDoc(10, 65537, "Mail 1", new Dictionary<string, string>(), FileFetchResult.Ok(new List<string> { src }));

            var summary = new ExportRunner(fake, CreateTestConfig(exportDir))
                .Run(new[] { new EnaioDocument { Id = 10, Type = 65537 } });

            AssertEqual(1, summary.ExportedDocuments);
            AssertEqual(1, summary.ExportedFiles);
            AssertEqual(0, summary.FailedDocuments);

            var files = Directory.GetFiles(summary.RunFolder);
            AssertEqual(1, files.Length, "Dateien im Laufordner");
            AssertEqual("0001_20250307_Grüße aus Köln.txt", Path.GetFileName(files[0]));
            Assert(!Directory.Exists(Path.Combine(summary.RunFolder, "_work")), "_work wurde nicht entfernt");
            Assert(!Directory.Exists(summary.ErrorFolder), "_fehler darf nicht existieren");
            Assert(File.Exists(src), "enaio-Quelldatei darf nicht angetastet werden");
            File.SetAttributes(src, FileAttributes.Normal);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    private static void TestExportRunnerImaMail()
    {
        string dir = CreateTempDir();
        try
        {
            string exportDir = Path.Combine(dir, "export");
            string src = WriteEml(dir, "000E3747.ima", EmlUmlauts);

            var fake = new FakeEnaioClient();
            fake.AddDoc(10, 393216, "Alte Mail", new Dictionary<string, string>(), FileFetchResult.Ok(new List<string> { src }));

            var summary = new ExportRunner(fake, CreateTestConfig(exportDir))
                .Run(new[] { new EnaioDocument { Id = 10, Type = 393216 } });

            AssertEqual(1, summary.ExportedFiles);
            AssertEqual(0, summary.SkippedNonDocuments);
            AssertEqual(0, summary.Issues.Count, "Hinweise");
            AssertEqual(1, Directory.GetFiles(summary.RunFolder).Length, "Dateien im Laufordner");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    private static void TestExportRunnerDeleteFlags()
    {
        string dir = CreateTempDir();
        try
        {
            string src = WriteEml(dir, "m.eml", EmlUmlauts);
            var fake = new FakeEnaioClient();
            var files = FileFetchResult.Ok(new List<string> { src });

            fake.AddDoc(1, 65537, "A", new Dictionary<string, string> { ["zahl1"] = "1" }, files);
            fake.AddDoc(2, 65537, "B", new Dictionary<string, string> { ["zahl1"] = "ja" }, files);
            fake.AddDoc(3, 65537, "C", new Dictionary<string, string> { ["zahl1"] = "0" }, files, systemDeleteMarked: true);
            fake.AddDoc(4, 65538, "D", new Dictionary<string, string> { ["zahl2"] = "1" }, files);
            fake.AddDoc(5, 65537, "E", new Dictionary<string, string> { ["zahl1"] = "0" }, files);

            var config = CreateTestConfig(Path.Combine(dir, "export"));
            config.DeleteFlagFieldByType["65538"] = "zahl2";

            var summary = new ExportRunner(fake, config).Run(new[]
            {
                new EnaioDocument { Id = 1, Type = 65537 },
                new EnaioDocument { Id = 2, Type = 65537 },
                new EnaioDocument { Id = 3, Type = 65537 },
                new EnaioDocument { Id = 4, Type = 65538 },
                new EnaioDocument { Id = 5, Type = 65537 }
            });

            AssertEqual(3, summary.SkippedDeleteFlag, "Löschflag");
            AssertEqual(1, summary.SkippedSystemDelete, "System-Löschvormerkung");
            AssertEqual(1, summary.ExportedDocuments, "Exportiert");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    private static void TestExportRunnerNonDocuments()
    {
        string dir = CreateTempDir();
        try
        {
            var fake = new FakeEnaioClient();
            fake.SetKind(20, 1, EnaioObjectKind.Folder, "Schrank");
            fake.SetKind(21, 2, EnaioObjectKind.Register, "Register");

            var summary = new ExportRunner(fake, CreateTestConfig(Path.Combine(dir, "export")))
                .Run(new[] { new EnaioDocument { Id = 20, Type = 1 }, new EnaioDocument { Id = 21, Type = 2 } });

            AssertEqual(2, summary.SkippedNonDocuments);
            AssertEqual(0, summary.ExportedDocuments);
            Assert(summary.Issues.All(i => i.Kind == ExportIssueKind.NonDocument), "Falsche Issue-Art");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    private static void TestExportRunnerFileErrors()
    {
        string dir = CreateTempDir();
        try
        {
            var fake = new FakeEnaioClient();
            fake.AddDoc(30, 65537, "Leer", new Dictionary<string, string>(), FileFetchResult.NoFiles());
            fake.AddDoc(31, 65537, "Fehler", new Dictionary<string, string>(), FileFetchResult.Fail(-7, "Typ unbekannt"));

            var summary = new ExportRunner(fake, CreateTestConfig(Path.Combine(dir, "export")))
                .Run(new[] { new EnaioDocument { Id = 30, Type = 65537 }, new EnaioDocument { Id = 31, Type = 65537 } });

            AssertEqual(1, summary.EmptyDocuments);
            AssertEqual(1, summary.FailedDocuments);
            AssertEqual(0, summary.ExportedDocuments);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    private static void TestExportRunnerCorruptMail()
    {
        string dir = CreateTempDir();
        try
        {
            string bad = Path.Combine(dir, "bad.msg");
            File.WriteAllText(bad, "das ist keine msg-Datei");

            var fake = new FakeEnaioClient();
            fake.AddDoc(40, 65537, "Defekt", new Dictionary<string, string>(), FileFetchResult.Ok(new List<string> { bad }));

            var summary = new ExportRunner(fake, CreateTestConfig(Path.Combine(dir, "export")))
                .Run(new[] { new EnaioDocument { Id = 40, Type = 65537 } });

            AssertEqual(1, summary.FailedDocuments);
            AssertEqual(0, summary.ExportedDocuments);
            AssertEqual(1, summary.FilesMovedToErrorFolder);
            AssertEqual(1, Directory.GetFiles(summary.ErrorFolder).Length, "Dateien im Fehlerordner");
            Assert(!Directory.Exists(Path.Combine(summary.RunFolder, "_work")), "_work wurde nicht entfernt");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    private static void TestExportRunnerCancellation()
    {
        string dir = CreateTempDir();
        try
        {
            var fake = new FakeEnaioClient();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var summary = new ExportRunner(fake, CreateTestConfig(Path.Combine(dir, "export")))
                .Run(new[] { new EnaioDocument { Id = 70, Type = 65537 } }, cancellationToken: cts.Token);

            Assert(summary.WasCancelled, "Abbruch wurde nicht erkannt");
            AssertEqual(0, summary.ExportedDocuments);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    private static ExportConfig CreateTestConfig(string exportDir) => new()
    {
        ExportPath = exportDir,
        DeleteFlagField = "zahl1",
        CheckSystemDeleteFlag = true,
        MaxFileNameLength = 140
    };

    private sealed class FakeEnaioClient : IEnaioClient
    {
        private readonly Dictionary<(long, long), Dictionary<string, string>> metadata = new();
        private readonly Dictionary<(long, long), FileFetchResult> files = new();
        private readonly Dictionary<(long, long), EnaioObjectKind> kinds = new();
        private readonly Dictionary<(long, long), string> titles = new();
        private readonly HashSet<(long, long)> systemDeleted = new();

        public void AddDoc(
            long id,
            long type,
            string title,
            Dictionary<string, string> meta,
            FileFetchResult fileResult,
            bool systemDeleteMarked = false)
        {
            metadata[(id, type)] = new Dictionary<string, string>(meta, StringComparer.OrdinalIgnoreCase);
            files[(id, type)] = fileResult;
            kinds[(id, type)] = EnaioObjectKind.Document;
            titles[(id, type)] = title;
            if (systemDeleteMarked)
                systemDeleted.Add((id, type));
        }

        public void SetKind(long id, long type, EnaioObjectKind kind, string title)
        {
            kinds[(id, type)] = kind;
            titles[(id, type)] = title;
        }

        public string GetUserName() => "TestUser";
        public string GetClientVersion() => "12.0.0.0";
        public string GetCurrentSelection() => "";
        public EnaioObjectKind GetObjectKind(EnaioDocument doc) => kinds.GetValueOrDefault((doc.Id, doc.Type), EnaioObjectKind.Document);
        public string? GetObjectTitle(EnaioDocument doc) => titles.GetValueOrDefault((doc.Id, doc.Type));
        public bool IsMarkedForSystemDelete(EnaioDocument doc) => systemDeleted.Contains((doc.Id, doc.Type));
        public Dictionary<string, string> GetMetadata(long id, long type) => metadata[(id, type)];
        public FileFetchResult GetFiles(EnaioDocument doc) => files[(doc.Id, doc.Type)];
        public EnaioDocument GetFolderFromDocument(EnaioDocument doc) => throw new NotSupportedException();
        public void Dispose() { }
    }
}