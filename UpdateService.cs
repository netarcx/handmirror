using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using WpfApp = System.Windows.Application;

namespace HandMirror;

/// <summary>
/// Checks GitHub Releases for a newer build and applies it by running the silent
/// installer (which kills this instance, upgrades in place, and relaunches).
/// </summary>
public static class UpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/netarcx/handmirror/releases/latest";
    private const string InstallerAssetName = "HandMirrorSetup.exe";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub's API rejects requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HandMirror-Updater");
        return http;
    }

    public sealed record UpdateInfo(Version Version, string DownloadUrl);

    /// <summary>The version of the running app, normalized to major.minor.build.</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return Normalize(v);
        }
    }

    /// <summary>Returns the available update, or null if none / on any failure.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        string json;
        try
        {
            json = await Http.GetStringAsync(LatestReleaseApi);
        }
        catch
        {
            return null; // offline, rate-limited, no releases yet, etc.
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
            var latest = ParseVersion(tagEl.GetString());
            if (latest == null || latest <= CurrentVersion) return null;

            if (!root.TryGetProperty("assets", out var assets)) return null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var nameEl)
                    && string.Equals(nameEl.GetString(), InstallerAssetName, StringComparison.OrdinalIgnoreCase)
                    && asset.TryGetProperty("browser_download_url", out var urlEl)
                    && urlEl.GetString() is { Length: > 0 } url)
                {
                    return new UpdateInfo(latest, url);
                }
            }
        }
        catch
        {
            // Malformed payload — treat as "no update".
        }
        return null;
    }

    /// <summary>Downloads the installer to a temp file and returns its path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info)
    {
        var path = Path.Combine(Path.GetTempPath(), $"HandMirrorSetup-{info.Version}.exe");

        await using (var stream = await Http.GetStreamAsync(info.DownloadUrl))
        await using (var file = File.Create(path))
        {
            await stream.CopyToAsync(file);
        }

        return path;
    }

    /// <summary>
    /// Launches the installer silently and shuts down the app so it can be replaced.
    /// The installer kills any leftover instance and relaunches us when done.
    /// </summary>
    public static void ApplyAndRestart(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = true,
        });

        WpfApp.Current?.Shutdown();
    }

    private static Version Normalize(Version v) =>
        new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        // Tags look like "v1.1.42"; strip any leading non-digits.
        var trimmed = tag.TrimStart('v', 'V', ' ');
        return Version.TryParse(trimmed, out var v) ? Normalize(v) : null;
    }
}
