using Microsoft.AspNetCore.RateLimiting;
using YtGrab.Models;
using YtGrab.Services;

namespace YtGrab.Endpoints;

public static class ApiEndpoints
{
    public static string ClientIp(this HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/info", GetInfo).RequireRateLimiting("heavy");
        api.MapPost("/download", StartDownload).RequireRateLimiting("heavy");
        api.MapGet("/jobs/{id}", GetJob);
        api.MapGet("/jobs/{id}/file", GetFile);
    }

    private static async Task<IResult> GetInfo(InfoRequest req, YtDlpService yt, CancellationToken ct)
    {
        if (!YouTubeUrl.TryNormalize(req.Url, out var url))
            return Results.BadRequest(new ApiError("Please enter a valid YouTube video link."));

        try
        {
            return Results.Ok(await yt.GetInfoAsync(url, ct));
        }
        catch (YtDlpException ex)
        {
            return Results.UnprocessableEntity(new ApiError(ex.Message));
        }
    }

    private static async Task<IResult> StartDownload(
        DownloadRequest req, HttpContext ctx, YtDlpService yt, JobManager jobs, CancellationToken ct)
    {
        if (!YouTubeUrl.TryNormalize(req.Url, out var url))
            return Results.BadRequest(new ApiError("Please enter a valid YouTube video link."));

        var ip = ctx.ClientIp();
        if (jobs.ActiveCountForIp(ip) >= 2)
            return Results.Json(new ApiError("You already have downloads in progress. Please wait for them to finish."),
                statusCode: StatusCodes.Status429TooManyRequests);

        VideoInfo info;
        try
        {
            info = await yt.GetInfoAsync(url, ct); // cached after /info
        }
        catch (YtDlpException ex)
        {
            return Results.UnprocessableEntity(new ApiError(ex.Message));
        }

        if (req.Kind == DownloadKind.Mp4 &&
            (req.Height is null || !info.Heights.Contains(req.Height.Value)))
        {
            return Results.BadRequest(new ApiError("The selected quality is not available for this video."));
        }

        var job = new DownloadJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Url = url,
            Kind = req.Kind,
            Height = req.Kind == DownloadKind.Mp4 ? req.Height : null,
            Title = info.Title,
            OwnerIp = ip
        };

        if (!jobs.TryEnqueue(job))
            return Results.Json(new ApiError("The server is busy. Please try again in a minute."),
                statusCode: StatusCodes.Status503ServiceUnavailable);

        return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
    }

    private static IResult GetJob(string id, JobManager jobs)
    {
        var job = jobs.Get(id);
        if (job is null) return Results.NotFound(new ApiError("This download has expired. Please start again."));

        return Results.Ok(new JobStatusDto(
            job.State,
            job.Progress,
            job.Stage,
            job.Error,
            job.State == JobState.Completed ? $"/api/jobs/{job.Id}/file" : null));
    }

    private static IResult GetFile(string id, JobManager jobs)
    {
        var job = jobs.Get(id);
        var path = job?.FilePath;

        if (job is null || job.State != JobState.Completed || path is null || !File.Exists(path))
            return Results.NotFound(new ApiError("File not found or expired."));

        var isMp3 = job.Kind == DownloadKind.Mp3;
        var name = FileHelper.SafeName(job.Title)
                   + (isMp3 ? "" : $" [{job.Height}p]")
                   + (isMp3 ? ".mp3" : ".mp4");

        // FileShare.Delete lets the cleanup service remove the file even while a client is still reading it.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        return Results.File(stream, isMp3 ? "audio/mpeg" : "video/mp4", name, enableRangeProcessing: true);
    }
}