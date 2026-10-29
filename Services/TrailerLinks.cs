using System.Text.RegularExpressions;
using System.Web;

namespace CinemaApp.Services;

// Đổi mọi kiểu link YouTube (watch?v=, youtu.be/, shorts/, embed/) sang dạng nhúng được vào khung phát
public static class TrailerLinks
{
    private static readonly Regex VideoId = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    public static string Normalize(string? url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;

        var host = u.Host.ToLowerInvariant();
        string? id = null;

        if (host == "youtu.be")
        {
            id = u.AbsolutePath.Trim('/').Split('/')[0];
        }
        else if (host == "youtube.com" || host.EndsWith(".youtube.com") || host == "youtube-nocookie.com" || host.EndsWith(".youtube-nocookie.com"))
        {
            var path = u.AbsolutePath;
            if (path.StartsWith("/embed/")) id = path.Substring(7).Split('/')[0];
            else if (path.StartsWith("/shorts/")) id = path.Substring(8).Split('/')[0];
            else if (path.StartsWith("/live/")) id = path.Substring(6).Split('/')[0];
            else if (path == "/watch") id = HttpUtility.ParseQueryString(u.Query)["v"];
        }

        return id != null && VideoId.IsMatch(id) ? "https://www.youtube.com/embed/" + id : url;
    }
}