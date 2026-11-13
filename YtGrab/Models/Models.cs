namespace YtGrab.Models;

public enum DownloadKind { Mp4, Mp3 }

public enum JobState { Queued, Running, Completed, Failed }

public sealed record InfoRequest(string? Url);

public sealed record DownloadRequest(string? Url, DownloadKind Kind, int? Height);

public sealed record ApiError(string Error);

public sealed record VideoInfo(
    string Id,
    string Title,
    string? Channel,
    int DurationSeconds,
    string Thumbnail,
    IReadOnlyList<int> Heights);

public sealed record JobStatusDto(
    JobState State,
    int Progress,
    string Stage,
    string? Error,
    string? DownloadUrl);