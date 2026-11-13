using Microsoft.Extensions.Options;
using YtGrab.Configuration;
using YtGrab.Models;

namespace YtGrab.Services;

public sealed class DownloadWorker : BackgroundService
{
    private readonly JobManager _jobs;
    private readonly YtDlpService _yt;
    private readonly DownloaderOptions _opt;
    private readonly ILogger<DownloadWorker> _log;

    public DownloadWorker(JobManager jobs, YtDlpService yt, IOptions<DownloaderOptions> options, ILogger<DownloadWorker> log)
    {
        _jobs = jobs;
        _yt = yt;
        _opt = options.Value;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stop)
    {
        var consumers = Enumerable.Range(0, Math.Max(1, _opt.MaxConcurrentJobs))
                                  .Select(_ => ConsumeAsync(stop));
        return Task.WhenAll(consumers);
    }

    private async Task ConsumeAsync(CancellationToken stop)
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync(stop))
            await ProcessAsync(job, stop);
    }

    private async Task ProcessAsync(DownloadJob job, CancellationToken stop)
    {
        var dir = Path.Combine(_opt.ResolveTempRoot(), job.Id);
        Directory.CreateDirectory(dir);

        job.Stage = "Downloading";
        job.State = JobState.Running;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        cts.CancelAfter(TimeSpan.FromMinutes(_opt.JobTimeoutMinutes));

        try
        {
            var file = await _yt.DownloadAsync(job, dir, cts.Token);
            job.FilePath = file;
            job.Progress = 100;
            job.Stage = "Done";
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.State = JobState.Completed; // set last
        }
        catch (YtDlpException ex)
        {
            Fail(job, dir, ex.Message);
        }
        catch (OperationCanceledException)
        {
            Fail(job, dir, stop.IsCancellationRequested ? "The server is shutting down." : "The download timed out.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unexpected error in job {JobId}", job.Id);
            Fail(job, dir, "An unexpected error occurred.");
        }
    }

    private static void Fail(DownloadJob job, string dir, string message)
    {
        job.Error = message;
        job.Stage = "Failed";
        job.FinishedAt = DateTimeOffset.UtcNow;
        job.State = JobState.Failed;
        FileHelper.TryDeleteDirectory(dir);
    }
}