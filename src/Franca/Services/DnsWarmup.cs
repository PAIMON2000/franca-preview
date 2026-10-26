using System.Net;

namespace Franca.Services;

// DNS 预热：用户网络首个 DNS 查询经常超时重试，每个新域名首连要付数秒"首连税"
// 后台并行解析全部端点域名，让解析并行进行而不是在测速时串行排队；全部失败静默
public static class DnsWarmup
{
    private static readonly string[] Hosts =
    {
        "www.bing.com",
        "translate.googleapis.com",
        "translate.yandex.net",
        "www.deepl.com",
        "www2.deepl.com",
        "w.deepl.com",
        "papago.naver.com",
        "transmart.qq.com",
        "fanyi.youdao.com",
        "dict.youdao.com",
        "fanyi.sogou.com",
        "api.mymemory.translated.net",
        "store.steampowered.com",
    };

    // fire-and-forget：调用方不等它完成，测速请求本身会等 DNS
    public static void KickOff() => _ = Task.Run(() => Task.WhenAll(Hosts.Select(ResolveAsync)));

    private static async Task ResolveAsync(string host)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Dns.GetHostAddressesAsync(host, cts.Token);
        }
        catch
        {
            // 单个域名解析失败静默，不影响任何主流程
        }
    }
}
