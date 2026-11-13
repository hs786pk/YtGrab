using Microsoft.Extensions.Options;
using YtGrab.Configuration;

namespace YtGrab.Services;

/// <summary>YouTube changes constantly; keeping yt-dlp current is what keeps the site working.</summary>
public sealed class YtDlpUpdater(
    YtDlpService yt,
    IOptions<DownloaderOptions> options,
    ILogger<YtDlpUpdater> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.Value.AutoUpdateYtDlp) return;

        await Task.Delay(TimeSpan.FromSeconds(10), stop);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(12));
        do
        {
            try
            {
                log.LogInformation("yt-dlp update check: {Result}", await yt.UpdateAsync(stop));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogWarning(ex, "yt-dlp update check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stop));
    }
}