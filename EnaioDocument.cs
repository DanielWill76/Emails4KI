using System;
using System.Collections.Generic;

public class EnaioDocument
{
    public long Id { get; set; }
    public long Type { get; set; }
}

public enum EnaioObjectKind
{
    Document,
    Folder,
    Register,
    Unknown
}

public enum FileFetchStatus
{
    Success,
    Empty,
    Error
}

public class FileFetchResult
{
    public FileFetchStatus Status { get; init; }
    public List<string> Files { get; init; } = new();
    public int ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static FileFetchResult Ok(List<string> files) => new()
    {
        Status = FileFetchStatus.Success,
        Files = files
    };

    public static FileFetchResult NoFiles() => new()
    {
        Status = FileFetchStatus.Empty
    };

    public static FileFetchResult Fail(int errorCode, string message) => new()
    {
        Status = FileFetchStatus.Error,
        ErrorCode = errorCode,
        ErrorMessage = message
    };
}

public interface IEnaioClient : IDisposable
{
    string GetUserName();
    string GetClientVersion();
    string GetCurrentSelection();
    EnaioObjectKind GetObjectKind(EnaioDocument doc);
    string? GetObjectTitle(EnaioDocument doc);
    bool IsMarkedForSystemDelete(EnaioDocument doc);
    Dictionary<string, string> GetMetadata(long id, long type);
    FileFetchResult GetFiles(EnaioDocument doc);
    EnaioDocument GetFolderFromDocument(EnaioDocument doc);
}
