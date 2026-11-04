using System.Net.Http;

namespace Franca.Services;

// GitHub 请求（更新检查/公告）共用的客户端：默认直连（与历史行为一致），
// 用户在设置页填写代理后走代理；设置页字段见 AppSettings.ProxyUrl。
internal static class GitHubHttp
{
    // 拉取结果缓存窗口：8 小时内页面选中/启动检查不重复联网
    public const long FreshWindowSeconds = 8 * 3600;

    public static bool IsFresh(long fetchedUtcSeconds) =>
        fetchedUtcSeconds > 0
        && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - fetchedUtcSeconds < FreshWindowSeconds;

    public static HttpClient Create(TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler { UseProxy = false };
        var proxy = GameNameService.ProxyOverride;
        if (!string.IsNullOrWhiteSpace(proxy)
            && Uri.TryCreate(proxy.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            handler.UseProxy = true;
            handler.Proxy = new System.Net.WebProxy(uri);
        }
        return new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(8) };
    }
}
