namespace YtGrab.Models;

public sealed class DownloadJob
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public required DownloadKind Kind { get; init; }
    public int? Height { get; init; }
    public required string Title { get; init; }
    public required string OwnerIp { get; init; }

    // Written by worker thread, read by request threads -> volatile.
    public volatile JobState State = JobState.Queued;
    public volatile int Progress;
    public volatile string Stage = "Queued";
    public volatile string? Error;
    public volatile string? FilePath;

    public DateTimeOffset? FinishedAt { get; set; }
}