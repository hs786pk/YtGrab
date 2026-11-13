using Microsoft.Extensions.Options;
using YtGrab.Configuration;

namespace YtGrab.Services;

public sealed class CleanupService(
    JobManager jobs,
    IOptions<DownloaderOptions> options,
    ILogger<CleanupService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var o = options.Value;
        var root = o.ResolveTempRoot();
        Directory.CreateDirectory(root);

        // Remove leftovers from a previous run/crash.
        foreach (var d in Directory.EnumerateDirectories(root))
            FileHelper.TryDeleteDirectory(d);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stop))
        {
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-o.FileRetentionMinutes);

            foreach (var job in jobs.All.ToList())
            {
                if (job.FinishedAt is { } finished && finished < cutoff)
                {
                    FileHelper.TryDeleteDirectory(Path.Combine(root, job.Id));
                    jobs.Remove(job.Id);
                    log.LogDebug("Cleaned up job {JobId}", job.Id);
                }
            }
        }
    }
}