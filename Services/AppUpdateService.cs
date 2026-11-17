using System.Net.Http;
using System.Text.Json;

namespace PcrBattleAssistant.Services;

public sealed record AppUpdateCheckResult(
    bool HasUpdate,
    string LatestTag,
    string ReleaseUrl);

/// <summary>
/// GitHub Releases 版本检查服务（仅检查最新版本并跳转发布页）
/// </summary>
public static class AppUpdateService
{
    public const string RepoOwner = "soniczwei";
    public const string RepoName = "PcrBattleAssistant";
    public const string ReleasesPageUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases";
    private const string LatestReleaseApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    private static readonly HttpClient GitHubClient = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{RepoName}/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<AppUpdateCheckResult> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await GitHubClient.GetAsync(LatestReleaseApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new AppUpdateCheckResult(false, string.Empty, ReleasesPageUrl);
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagEl)
                ? (tagEl.GetString() ?? string.Empty).Trim()
                : string.Empty;

            string htmlUrl = root.TryGetProperty("html_url", out var urlEl)
                ? (urlEl.GetString() ?? ReleasesPageUrl)
                : ReleasesPageUrl;

            if (TryParseTagVersion(tagName, out Version? remoteVersion) &&
                remoteVersion is not null &&
                IsNewerVersion(remoteVersion, currentVersion))
            {
                string displayTag = tagName.StartsWith('v') || tagName.StartsWith('V')
                    ? tagName
                    : $"v{tagName}";
                return new AppUpdateCheckResult(true, displayTag, htmlUrl);
            }
        }
        catch
        {
            // 网络异常或尚未发布 Release 时静默忽略
        }

        return new AppUpdateCheckResult(false, string.Empty, ReleasesPageUrl);
    }

    private static bool TryParseTagVersion(string tagName, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return false;
        }

        string cleaned = tagName.TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(cleaned, out version);
    }

    private static bool IsNewerVersion(Version remote, Version current)
    {
        Version r = new(remote.Major, Math.Max(0, remote.Minor), Math.Max(0, remote.Build));
        Version c = new(current.Major, Math.Max(0, current.Minor), Math.Max(0, current.Build));
        return r > c;
    }
}
