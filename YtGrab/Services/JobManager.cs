using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using YtGrab.Configuration;
using YtGrab.Models;

namespace YtGrab.Services;

public sealed class JobManager
{
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly Channel<DownloadJob> _queue;

    public JobManager(IOptions<DownloaderOptions> options)
    {
        _queue = Channel.CreateBounded<DownloadJob>(new BoundedChannelOptions(options.Value.MaxQueuedJobs)
        {
            SingleReader = false,
            SingleWriter = false
        });
    }

    public ChannelReader<DownloadJob> Reader => _queue.Reader;

    public IEnumerable<DownloadJob> All => _jobs.Values;

    public bool TryEnqueue(DownloadJob job)
    {
        _jobs[job.Id] = job;
        if (_queue.Writer.TryWrite(job)) return true;

        _jobs.TryRemove(job.Id, out _);
        return false;
    }

    public DownloadJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public bool Remove(string id) => _jobs.TryRemove(id, out _);

    public int ActiveCountForIp(string ip) =>
        _jobs.Values.Count(j => j.OwnerIp == ip && j.State is JobState.Queued or JobState.Running);
}