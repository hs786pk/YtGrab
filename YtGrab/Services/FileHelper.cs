using System.Text;

namespace YtGrab.Services;

public static class FileHelper
{
    private const string Bad = "\\/:*?\"<>|";

    public static string SafeName(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (var ch in title)
            if (!char.IsControl(ch) && !Bad.Contains(ch))
                sb.Append(ch);

        var s = sb.ToString().Trim().TrimEnd('.');
        if (s.Length > 100) s = s[..100].TrimEnd();
        return s.Length == 0 ? "video" : s;
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // File may still be in use; the cleanup service will retry later.
        }
    }
}