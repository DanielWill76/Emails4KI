using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

public class ExportConfig
{
    public const string ConfigFileName = "Emails4KI.json";
    public const string EnvVarPrefix = "EMAILS4KI_";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    public string ExportPath { get; set; } = "";
    public string RunFolderPattern { get; set; } = "Export_yyyyMMdd_HHmmss";
    public string DeleteFlagField { get; set; } = "zahl1";
    public Dictionary<string, string> DeleteFlagFieldByType { get; set; } = new();
    public bool CheckSystemDeleteFlag { get; set; } = true;
    public int MaxFileNameLength { get; set; } = 140;
    public bool Utf8Bom { get; set; } = true;
    public bool AutoOpenExportFolders { get; set; } = true;
    public LoggingSettings FileLogging { get; set; } = new();

    public string GetDeleteFlagField(long objectType)
    {
        if (DeleteFlagFieldByType != null &&
            DeleteFlagFieldByType.TryGetValue(objectType.ToString(), out string? field) &&
            !string.IsNullOrWhiteSpace(field))
            return field.Trim();

        return string.IsNullOrWhiteSpace(DeleteFlagField) ? "zahl1" : DeleteFlagField.Trim();
    }

    public static IConfigurationRoot BuildConfiguration(string baseDirectory)
    {
        string configPath = Path.Combine(baseDirectory, ConfigFileName);
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Konfigurationsdatei nicht gefunden: {configPath}", configPath);

        return new ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile(ConfigFileName, optional: false, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: EnvVarPrefix)
            .Build();
    }

    public static ExportConfig LoadFromConfiguration(IConfiguration configuration)
    {
        var config = configuration.Get<ExportConfig>()
            ?? throw new InvalidOperationException($"Konfigurationsdatei {ConfigFileName} konnte nicht gelesen werden.");

        config.FileLogging = LoggingSettings.BindFrom(configuration);
        return ValidateAndNormalize(config);
    }

    public static ExportConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Konfigurationsdatei nicht gefunden: {path}", path);

        string json = File.ReadAllText(path);
        return Parse(json);
    }

    public static ExportConfig Parse(string json)
    {
        var config = JsonSerializer.Deserialize<ExportConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException("Konfigurationsdatei konnte nicht gelesen werden.");

        return ValidateAndNormalize(config);
    }

    private static ExportConfig ValidateAndNormalize(ExportConfig config)
    {
        config.ExportPath = Environment.ExpandEnvironmentVariables(config.ExportPath ?? "").Trim();
        if (string.IsNullOrWhiteSpace(config.ExportPath))
            throw new InvalidOperationException($"ExportPath darf in der {ConfigFileName} nicht leer sein.");

        if (config.MaxFileNameLength < 20)
            config.MaxFileNameLength = 20;

        config.FileLogging ??= new LoggingSettings();
        if (string.IsNullOrWhiteSpace(config.FileLogging.Directory))
            config.FileLogging.Directory = LoggingSettings.DefaultDirectory;
        if (config.FileLogging.RetentionDays < 0)
            config.FileLogging.RetentionDays = 0;

        if (string.IsNullOrWhiteSpace(config.RunFolderPattern))
            config.RunFolderPattern = "Export_yyyyMMdd_HHmmss";
        else
            _ = DateTime.Now.ToString(config.RunFolderPattern);

        config.DeleteFlagFieldByType ??= new Dictionary<string, string>();
        return config;
    }
}

