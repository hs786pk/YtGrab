namespace YtGrab.Configuration;

public sealed class DownloaderOptions
{
    public const string SectionName = "Downloader";

    // Windows: bundled exe in /tools. Linux: expects binaries on PATH (Docker image installs them).
    public string YtDlpPath { get; set; } = OperatingSystem.IsWindows() ? "tools/yt-dlp.exe" : "yt-dlp";
    public string FfmpegPath { get; set; } = OperatingSystem.IsWindows() ? "tools/ffmpeg.exe" : "ffmpeg";

    /// <summary>Empty = system temp folder + /ytgrab</summary>
    public string TempRoot { get; set; } = "";

    public int MaxConcurrentJobs { get; set; } = 2;
    public int MaxQueuedJobs { get; set; } = 50;
    public int MaxDurationMinutes { get; set; } = 120;
    public int MaxFileSizeMb { get; set; } = 2048;
    public int FileRetentionMinutes { get; set; } = 30;
    public int JobTimeoutMinutes { get; set; } = 20;
    public int InfoTimeoutSeconds { get; set; } = 45;
    public bool AutoUpdateYtDlp { get; set; } = true;

    /// <summary>Optional Netscape-format cookies file (helps when YouTube blocks server IPs).</summary>
    public string? CookiesFile { get; set; }

    /// <summary>Optional proxy, e.g. http://user:pass@host:port</summary>
    public string? Proxy { get; set; }

    public string ResolveTempRoot() =>
        string.IsNullOrWhiteSpace(TempRoot) ? Path.Combine(Path.GetTempPath(), "ytgrab") : TempRoot;
}