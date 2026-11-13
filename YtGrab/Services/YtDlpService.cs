using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using YtGrab.Configuration;
using YtGrab.Models;

namespace YtGrab.Services;

public sealed partial class YtDlpService
{
    private readonly DownloaderOptions _opt;
    private readonly IMemoryCache _cache;
    private readonly ILogger<YtDlpService> _log;
    private readonly string _ytDlpPath;
    private readonly string? _ffmpegPath;

    private sealed record ProcResult(int ExitCode, string StdOut, string StdErr);

    [GeneratedRegex(@"\[download\]\s+(\d{1,3}(?:\.\d+)?)%")]
    private static partial Regex ProgressRegex();

    public YtDlpService(IOptions<DownloaderOptions> options, IMemoryCache cache, ILogger<YtDlpService> log)
    {
        _opt = options.Value;
        _cache = cache;
        _log = log;
        _ytDlpPath = ResolveExecutable(_opt.YtDlpPath);

        var ff = ResolveExecutable(_opt.FfmpegPath);
        _ffmpegPath = !string.IsNullOrEmpty(ff) && File.Exists(ff) ? ff : null; // null => use PATH
    }

    // ---------- Public API ----------

    public async Task<VideoInfo> GetInfoAsync(string url, CancellationToken ct)
    {
        if (_cache.TryGetValue(url, out VideoInfo? cached) && cached is not null)
            return cached;

        var args = BaseArgs();
        args.Add("--dump-single-json");
        args.Add("--skip-download");
        args.Add("--");
        args.Add(url);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_opt.InfoTimeoutSeconds));

        ProcResult r;
        try
        {
            r = await RunAsync(args, null, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new YtDlpException("Timed out while reading the video. Please try again.");
        }

        if (r.ExitCode != 0)
        {
            _log.LogWarning("yt-dlp info failed ({Code}): {Err}", r.ExitCode, r.StdErr);
            throw new YtDlpException(Friendly(r.StdErr));
        }

        using var doc = JsonDocument.Parse(r.StdOut);
        var root = doc.RootElement;

        if (root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True)
            throw new YtDlpException("Live streams are not supported.");

        var id = root.GetProperty("id").GetString() ?? throw new YtDlpException("Could not read video information.");
        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? id : id;

        string? channel = null;
        if (root.TryGetProperty("channel", out var ch) && ch.ValueKind == JsonValueKind.String) channel = ch.GetString();
        else if (root.TryGetProperty("uploader", out var up) && up.ValueKind == JsonValueKind.String) channel = up.GetString();

        var duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? (int)d.GetDouble()
            : 0;

        if (duration > _opt.MaxDurationMinutes * 60)
            throw new YtDlpException($"Videos longer than {_opt.MaxDurationMinutes} minutes are not supported.");

        var heights = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a))); // descending
        if (root.TryGetProperty("formats", out var fmts) && fmts.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fmts.EnumerateArray())
            {
                if (!f.TryGetProperty("height", out var h) || h.ValueKind != JsonValueKind.Number) continue;
                if (!h.TryGetInt32(out var hv) || hv < 144) continue;
                if (f.TryGetProperty("vcodec", out var vc) && vc.ValueKind == JsonValueKind.String && vc.GetString() == "none") continue;
                heights.Add(hv);
            }
        }

        var info = new VideoInfo(id, title, channel, duration,
            $"https://i.ytimg.com/vi/{id}/hqdefault.jpg", heights.ToList());

        _cache.Set(url, info, TimeSpan.FromMinutes(10));
        return info;
    }

    /// <summary>Runs the download and returns the final file path.</summary>
    public async Task<string> DownloadAsync(DownloadJob job, string workDir, CancellationToken ct)
    {
        var args = BaseArgs();
        args.AddRange(["--newline", "--concurrent-fragments", "4",
                       "--max-filesize", $"{_opt.MaxFileSizeMb}M",
                       "-o", Path.Combine(workDir, "media.%(ext)s")]);

        if (job.Kind == DownloadKind.Mp3)
        {
            args.AddRange(["-f", "bestaudio/best", "-x", "--audio-format", "mp3", "--audio-quality", "0"]);
        }
        else
        {
            var h = job.Height!.Value;
            args.AddRange(["-f",
                $"bv*[height<={h}][ext=mp4]+ba[ext=m4a]/bv*[height<={h}]+ba/b[height<={h}]",
                "--merge-output-format", "mp4"]);
        }

        args.Add("--");
        args.Add(job.Url);

        void OnLine(string line)
        {
            var m = ProgressRegex().Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                // video + audio are downloaded separately, so keep progress monotonic and cap at 95.
                job.Progress = Math.Max(job.Progress, Math.Min(95, (int)pct));
            }
            else if (line.StartsWith("[Merger]") || line.StartsWith("[ExtractAudio]") ||
                     line.StartsWith("[VideoConvertor]") || line.StartsWith("[Fixup"))
            {
                job.Stage = "Processing";
                job.Progress = Math.Max(job.Progress, 96);
            }
        }

        var r = await RunAsync(args, OnLine, ct);

        if (r.ExitCode != 0)
        {
            _log.LogWarning("yt-dlp download failed ({Code}) for {Url}: {Err}", r.ExitCode, job.Url, r.StdErr);
            throw new YtDlpException(Friendly(r.StdErr));
        }

        var expected = Path.Combine(workDir, job.Kind == DownloadKind.Mp3 ? "media.mp3" : "media.mp4");
        if (!File.Exists(expected))
            throw new YtDlpException("The file could not be created (it may exceed the size limit).");

        return expected;
    }

    public async Task<string> UpdateAsync(CancellationToken ct)
    {
        var r = await RunAsync(["-U"], null, ct);
        return (r.StdOut + r.StdErr).Trim();
    }

    // ---------- Internals ----------

    private List<string> BaseArgs()
    {
        var a = new List<string>
        {
            "--ignore-config", "--no-playlist", "--no-warnings", "--no-color",
            "--socket-timeout", "20", "--retries", "3"
        };

        if (_ffmpegPath is not null) { a.Add("--ffmpeg-location"); a.Add(_ffmpegPath); }

        if (!string.IsNullOrWhiteSpace(_opt.CookiesFile) && File.Exists(_opt.CookiesFile))
        { a.Add("--cookies"); a.Add(_opt.CookiesFile); }

        if (!string.IsNullOrWhiteSpace(_opt.Proxy))
        { a.Add("--proxy"); a.Add(_opt.Proxy); }

        return a;
    }

    private async Task<ProcResult> RunAsync(IEnumerable<string> args, Action<string>? onStdoutLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ytDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // ArgumentList => no shell, no quoting issues, no injection.
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            onStdoutLine?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr) stderr.AppendLine(e.Data);
        };

        try
        {
            p.Start();
        }
        catch (Win32Exception ex)
        {
            _log.LogError(ex, "Could not start yt-dlp at {Path}", _ytDlpPath);
            throw new YtDlpException("The download engine is not available on the server.", ex);
        }

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }

        return new ProcResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string ResolveExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (Path.IsPathRooted(path)) return path;
        if (path.Contains('/') || path.Contains('\\')) return Path.Combine(AppContext.BaseDirectory, path);
        return path; // bare name -> resolved through PATH
    }

    private static string Friendly(string err)
    {
        bool Has(string s) => err.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (Has("not a bot") || Has("Sign in to confirm"))
            return "YouTube is temporarily limiting requests from this server. Please try again later.";
        if (Has("Private video")) return "This video is private.";
        if (Has("confirm your age") || Has("age-restricted")) return "This video is age-restricted and cannot be downloaded.";
        if (Has("not available in your country") || Has("blocked it")) return "This video is not available in the server's region.";
        if (Has("Video unavailable") || Has("has been removed")) return "This video is unavailable.";
        return "Could not process this video. Please try another link.";
    }
}