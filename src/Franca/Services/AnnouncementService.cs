using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Franca.Services;

internal sealed record Announcement(string Id, string Text);

// 公告：仓库根 ANNOUNCEMENT.md 全文为正文，首个 "## " 标题行作为公告标识（已读标记按标识比较）。
// 每次启动自动获取；按钮点击重新获取并在对话框展示，同时记为已读。
internal sealed class AnnouncementService
{
    public const string AnnouncementUrl =
        "https://raw.githubusercontent.com/PAIMON2000/franca-preview/main/ANNOUNCEMENT.md";

    public async Task<Announcement?> FetchAsync(CancellationToken ct, HttpClient? client = null)
    {
        using var owned = client is null ? GitHubHttp.Create() : null;
        client ??= owned!;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AnnouncementUrl);
            request.Headers.UserAgent.ParseAdd("Franca/" + ReleaseUpdateService.CurrentVersion);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(text);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            AppLog.Write("Announcement fetch unavailable: " + ex.GetType().Name);
            return null;
        }
    }

    // 首个 "## " 标题为公告标识；无标题或空文本视为无公告
    internal static Announcement? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var id = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("## ", StringComparison.Ordinal)) continue;
            id = line[3..].Trim();
            break;
        }
        return id.Length == 0 ? null : new Announcement(id, text.Trim());
    }
}
