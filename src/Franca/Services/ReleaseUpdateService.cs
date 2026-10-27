using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Franca.Services;

internal sealed record AvailableRelease(string Name, Version Version, Uri Page);

internal sealed class ReleaseUpdateService
{
    public const string RepositoryUrl = "https://github.com/PAIMON2000/franca-preview";
    public const string LatestApi = "https://api.github.com/repos/PAIMON2000/franca-preview/releases/latest";
    public static Version CurrentVersion => Version.Parse(typeof(ReleaseUpdateService).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ReleaseVersion").Value!);
    public static string DisplayVersion => typeof(ReleaseUpdateService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? CurrentVersion.ToString();

    public async Task<AvailableRelease?> CheckAsync(CancellationToken ct, HttpClient? client = null)
    {
        using var owned = client is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(8) } : null;
        client ??= owned!;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            request.Headers.UserAgent.ParseAdd("Franca/" + CurrentVersion);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseRelease(json, CurrentVersion);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            AppLog.Write("Release check unavailable: " + ex.GetType().Name);
            return null;
        }
    }

    internal static AvailableRelease? ParseRelease(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        if (!release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
            || !release.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False
            || !release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
            || !Version.TryParse(tag.GetString()?.TrimStart('v', 'V'), out var version)
            || version <= current
            || !release.TryGetProperty("html_url", out var url) || url.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var page)
            || page.Scheme != Uri.UriSchemeHttps || page.Host != "github.com" || !string.IsNullOrEmpty(page.UserInfo)
            || !page.AbsolutePath.StartsWith("/PAIMON2000/franca-preview/releases/tag/", StringComparison.Ordinal)
            || !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array
            || !assets.EnumerateArray().Any(a => a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                && n.GetString()!.StartsWith("Franca", StringComparison.OrdinalIgnoreCase)
                && n.GetString()!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            return null;
        var name = release.TryGetProperty("name", out var title) && title.ValueKind == JsonValueKind.String
            ? title.GetString() : null;
        return new AvailableRelease(string.IsNullOrWhiteSpace(name) ? tag.GetString()! : name, version, page);
    }
}
