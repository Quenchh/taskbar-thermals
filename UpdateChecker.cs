using System.Net.Http.Headers;
using System.Text.Json;

namespace TaskbarThermals;

/// <summary>Asks the GitHub API for the latest release. Sends nothing but a plain HTTPS request with a User-Agent.</summary>
internal static class UpdateChecker
{
    public const string Repository = "Quenchh/taskbar-thermals";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases/latest";

    public static Version Current
    {
        get
        {
            var v = typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>The latest release version if it's newer than this build, otherwise null. Never throws.</summary>
    public static async Task<Version?> FindNewerAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TaskbarThermals", Current.ToString()));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest"));
            string? tag = doc.RootElement.GetProperty("tag_name").GetString();
            if (!Version.TryParse(tag?.TrimStart('v', 'V'), out var parsed)) return null;
            var latest = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
            return latest > Current ? latest : null;
        }
        catch
        {
            return null; // offline, rate-limited, no releases yet: try again next time
        }
    }
}
