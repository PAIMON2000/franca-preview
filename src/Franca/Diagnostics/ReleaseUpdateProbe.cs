using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Franca.Services;

namespace Franca.Diagnostics;

internal static class ReleaseUpdateProbe
{
    public static async Task RunAsync()
    {
        var failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"RESULT\tRELEASE\t{name}\t{(ok ? "PASS" : "FAIL")}");
            if (!ok) failures++;
        }
        string Payload(string tag = "v0.2.20261028", bool draft = false, bool prerelease = false,
            string page = "https://github.com/hongweihu184/franca-preview/releases/tag/v0.2.20261028",
            string asset = "Franca0.2-1028.exe") => JsonSerializer.Serialize(new
            { tag_name = tag, name = "Franca next", draft, prerelease, html_url = page, assets = new[] { new { name = asset } } });
        var current = new Version(0, 1, 20261027);
        AvailableRelease? Parse(string value) => ReleaseUpdateService.ParseRelease(value, current);
        Check("version_metadata", ReleaseUpdateService.CurrentVersion == current
            && ReleaseUpdateService.DisplayVersion == "Franca0.1-1027");
        Check("new_release", Parse(Payload())?.Name == "Franca next");
        Check("equal_release", Parse(Payload("v0.1.20261027")) is null);
        Check("older_release", Parse(Payload("v0.1.20261026")) is null);
        Check("next_day", Parse(Payload("v0.1.20261028")) is not null);
        Check("draft", Parse(Payload(draft: true)) is null);
        Check("prerelease", Parse(Payload(prerelease: true)) is null);
        Check("invalid_tag", Parse(Payload("Franca0.1-1027")) is null);
        Check("foreign_url", Parse(Payload(page: "https://example.org/download")) is null);
        Check("other_repository", Parse(Payload(page: "https://github.com/other/repo/releases/tag/v1")) is null);
        Check("http_url", Parse(Payload(page: "http://github.com/hongweihu184/franca-preview/releases/tag/v1")) is null);
        Check("no_exe", Parse(Payload(asset: "source.zip")) is null);
        Check("incomplete_json", Parse("{}") is null);
        var handler = new StubHandler(Payload());
        using var client = new HttpClient(handler);
        var service = new ReleaseUpdateService();
        Check("request_contract", await service.CheckAsync(CancellationToken.None, client) is not null
            && handler.ValidRequest);
        handler.Status = HttpStatusCode.NotFound;
        Check("no_release", await service.CheckAsync(CancellationToken.None, client) is null);
        handler.Status = HttpStatusCode.Forbidden;
        Check("rate_limit", await service.CheckAsync(CancellationToken.None, client) is null);
        handler.Status = HttpStatusCode.OK;
        handler.Payload = "not JSON";
        Check("malformed_response", await service.CheckAsync(CancellationToken.None, client) is null);
        handler.Payload = "[]";
        Check("wrong_json_type", await service.CheckAsync(CancellationToken.None, client) is null);
        handler.ThrowOffline = true;
        Check("offline", await service.CheckAsync(CancellationToken.None, client) is null);
        handler.ThrowOffline = false;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Check("cancelled", await service.CheckAsync(cancelled.Token, client) is null);
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    private sealed class StubHandler(string payload) : HttpMessageHandler
    {
        public string Payload { get; set; } = payload;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool ValidRequest { get; private set; }
        public bool ThrowOffline { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (ThrowOffline) throw new HttpRequestException("Simulated offline");
            ValidRequest = request.RequestUri?.AbsoluteUri == ReleaseUpdateService.LatestApi
                && request.Headers.UserAgent.ToString().StartsWith("Franca/", StringComparison.Ordinal)
                && request.Headers.Accept.ToString().Contains("application/vnd.github+json");
            return Task.FromResult(new HttpResponseMessage(Status)
                { Content = new StringContent(Payload, Encoding.UTF8, "application/json") });
        }
    }
}
