using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Franca.Services;

namespace Franca.Diagnostics;

internal static class EndpointConfigurationProbe
{
    public static async Task RunAsync(bool liveDeepSeek = false)
    {
        var failures = 0;
        void Check(string name, bool value)
        {
            Console.WriteLine($"RESULT\tENDPOINT-CONFIG\t{name}\t{(value ? "PASS" : "FAIL")}");
            if (!value) failures++;
        }
        if (liveDeepSeek)
        {
            // 显式真调用模式：只读本机配置，不接受命令行密钥、不扫描游戏、不切换主用。
            var saved = AppSettings.Load();
            if (!saved.EndpointKeys.TryGetValue(EndpointConfigurationService.DeepSeekId, out var liveKeys)
                || !liveKeys.TryGetValue("ApiKey", out var key) || string.IsNullOrWhiteSpace(key))
            {
                Check("live_key_present", false);
                Environment.ExitCode = 1;
                return;
            }
            var result = await EndpointTestService.TestByokAsync(EndpointConfigurationService.DeepSeekId, liveKeys, "zh-CN", CancellationToken.None);
            Check("deepseek_health", result.Status is EndpointStatus.Ok or EndpointStatus.Slow);
            Console.WriteLine($"LIVE\tDeepSeek\t{result.Status}\t{result.LatencyMs}ms\t{result.Detail}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string? translated = null;
            try
            {
                translated = await AppTranslator.TranslateAsync(EndpointConfigurationService.DeepSeekId,
                    "Welcome back, Commander.", "zh-CN", liveKeys, timeout.Token);
            }
            catch (OperationCanceledException) { Console.WriteLine("LIVE\ttranslation\tTIMEOUT"); }
            Check("deepseek_production_translation", translated is not null && translated.Any(c => c is >= '\u4e00' and <= '\u9fff'));
            Console.WriteLine("LIVE\ttranslation\t" + (translated ?? "FAILED"));
            Environment.ExitCode = failures == 0 ? 0 : 1;
            return;
        }

        var settings = new AppSettings { IsTransient = true, Theme = "bluewhite", Endpoint = "LlmDeepSeek" };
        settings.EndpointKeys["other"] = new() { ["ApiKey"] = "other-sentinel" };
        AppSettings? published = null;
        settings.Saved += () => published = settings.Snapshot();
        EndpointConfigurationService.SaveDeepSeekKey(settings, "  fixture-key  ");
        Check("save_normalizes_key_and_model", settings.EndpointKeys["LlmDeepSeek"]["ApiKey"] == "fixture-key"
            && settings.EndpointKeys["LlmDeepSeek"]["Model"] == EndpointConfigurationService.DeepSeekModel);
        var oldSnapshot = settings.Snapshot();
        EndpointConfigurationService.DeleteKey(settings, "LlmDeepSeek");
        Check("delete_removes_next_request_key", published is not null && !published.EndpointKeys.ContainsKey("LlmDeepSeek"));
        Check("delete_falls_back", settings.Endpoint == "AutoFree");
        Check("delete_preserves_other_settings", settings.Theme == "bluewhite" && settings.EndpointKeys["other"]["ApiKey"] == "other-sentinel");
        Check("request_snapshot_isolated", oldSnapshot.EndpointKeys["LlmDeepSeek"]["ApiKey"] == "fixture-key");
        settings.Endpoint = "AutoLlm";
        EndpointConfigurationService.DeleteKey(settings, "LlmDeepSeek");
        Check("delete_auto_llm_falls_back", settings.Endpoint == "AutoFree");
        Check("prompt_has_no_player_secret", !EndpointConfigurationService.AgentPrompt.Contains("fixture-key")
            && !EndpointConfigurationService.AgentPrompt.Contains("other-sentinel"));
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Check("deleted_key_not_serialized", !restored.EndpointKeys.ContainsKey("LlmDeepSeek"));
        var invalidIndex = 0;
        foreach (var invalid in new[] { "", "null", "[]", "{}", "{\"name\":null}",
            "{\"name\":\"x\",\"model\":\"m\",\"baseUrl\":\"file:///C:/temp\"}",
            "{\"name\":\"x\",\"model\":\"m\",\"baseUrl\":\"http://public.example.com\"}",
            "{\"name\":\"x\",\"model\":\"m\",\"baseUrl\":\"https://api.example.com?key=fixture-key\"}",
            "{\"name\":\"x\",\"model\":\"m\",\"baseUrl\":\"https://api.example.com/chat/completions\"}",
            "{\"name\":\"x\",\"model\":\"m\",\"baseUrl\":\"https://api.example.com\",\"script\":\"ignored\"}" })
        {
            var rejected = false;
            try { EndpointConfigurationService.ParseModel(invalid); }
            catch (ArgumentException ex) { rejected = !ex.Message.Contains("fixture-key"); }
            Check("reject_invalid_config_" + invalidIndex++, rejected);
        }

        using var mock = new ModelFixture();
        var config = JsonSerializer.Serialize(new { name = "Fixture model", baseUrl = mock.Url, model = "fixture-model", apiKey = "fixture-key" });
        var imported = EndpointConfigurationService.ParseModel(config);
        Check("import_generates_id_and_group", imported.Id.StartsWith("custom:") && imported.Group == "Llm" && imported.Type == "openai");
        var endpointBefore = settings.Endpoint;
        EndpointConfigurationService.AddEndpoint(settings, imported);
        Check("import_does_not_switch_primary", settings.Endpoint == endpointBefore);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var text = await AppTranslator.TranslateCustomAsync(imported, "hello", "zh-CN", ct.Token, "en");
        Check("agent_import_production_request", text == "你好" && mock.LastModel == "fixture-model" && mock.LastAuth == "Bearer fixture-key");
        mock.Truncated = true;
        var partial = await AppTranslator.TranslateCustomAsync(imported, "hello", "zh-CN", ct.Token, "en");
        Check("truncated_translation_rejected", partial is null);
        mock.StatusCode = 402;
        var payment = await EndpointTestService.TestCustomOpenAiAsync(mock.Url, "fixture-key", "fixture-model", "hello", "en", "zh-CN", ct.Token);
        Check("payment_error_is_not_timeout", payment.Status == EndpointStatus.Failed && payment.Detail == "HTTP 402");
        Check("all_ui_languages_complete", !UiStrings.MissingKeys().Any());
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    private sealed class ModelFixture : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }
        public bool Truncated { get; set; }
        public int StatusCode { get; set; } = 200;
        public string? LastModel { get; private set; }
        public string? LastAuth { get; private set; }
        public ModelFixture()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = ServeAsync();
        }
        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                try
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
                    LastModel = json.RootElement.GetProperty("model").GetString();
                    LastAuth = context.Request.Headers["Authorization"];
                    context.Response.StatusCode = StatusCode;
                    var bytes = Encoding.UTF8.GetBytes("{\"choices\":[{\"finish_reason\":\"" + (Truncated ? "length" : "stop") + "\",\"message\":{\"content\":\"你好\"}}]}");
                    context.Response.ContentType = "application/json; charset=utf-8";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                }
                finally { context.Response.Close(); }
            }
        }
        public void Dispose() => _listener.Close();
    }
}
