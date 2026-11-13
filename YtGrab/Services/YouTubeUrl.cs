using System.Text.RegularExpressions;
using System.Web;

namespace YtGrab.Services;

/// <summary>
/// Strictly validates input and rebuilds a canonical URL, so user input
/// is never passed to yt-dlp as-is.
/// </summary>
public static partial class YouTubeUrl
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be"
    };

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();

    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = "";

        if (string.IsNullOrWhiteSpace(input) || input.Length > 300) return false;
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        if (!AllowedHosts.Contains(uri.Host)) return false;

        string? id = null;

        if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            id = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        else if (uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase))
        {
            id = HttpUtility.ParseQueryString(uri.Query)["v"];
        }
        else
        {
            var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length > 1 && segs[0] is "shorts" or "live" or "embed")
                id = segs[1];
        }

        if (id is null || !VideoIdRegex().IsMatch(id)) return false;

        normalized = $"https://www.youtube.com/watch?v={id}";
        return true;
    }
}