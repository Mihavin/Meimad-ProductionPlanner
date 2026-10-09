using System.Net;
using System.Text.Json;
using Meimad.Planner.Server.Application.LocalAi;
using Meimad.Planner.Server.Configuration;
namespace Meimad.Planner.Server.Tests.LocalAi;

public sealed class OllamaAdapterTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("wrong_digest")]
    [InlineData("wrong_runtime")]
    [InlineData("remote")]
    [InlineData("redirect")]
    [InlineData("oversize")]
    [InlineData("tool_call")]
    [InlineData("too_many_tokens")]
    public async Task Adapter_pins_local_artifacts_and_rejects_unbounded_or_unexpected_runtime_responses(string behavior)
    {
        var handler = new RuntimeHandler(behavior);
        var client = new OllamaModelClient(new LocalAiOptions { ModelId = "test:local", ModelDigest = new('a', 64), RuntimeVersion = "test-version" })
        { HandlerFactory = () => handler };
        if (behavior == "valid")
        {
            var result = await client.CompleteAsync(Bundle(), default);
            Assert.Equal("{\"claimOrder\":[0]}", result.Content); Assert.Equal(8, result.OutputTokens);
            Assert.Equal(new[] { "/api/version", "/api/tags", "/api/chat" }, handler.Paths);
        }
        else await Assert.ThrowsAnyAsync<Exception>(() => client.CompleteAsync(Bundle(), default));
        Assert.DoesNotContain(handler.Paths, path => path.Contains("pull") || path.Contains("create"));
    }
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(3, 8000, true)]
    [InlineData(3, 4000, false)]
    public async Task Resource_limits_reach_the_runtime_and_an_oversized_model_is_not_loaded(int threads, int memoryBudgetMb, bool allowed)
    {
        // CPU threads and keep-alive go with every request; a model file over the memory budget
        // (the runtime reports 5,000 MB here) is refused before /api/chat.
        var handler = new RuntimeHandler("valid") { ModelSizeBytes = 5000L * 1024 * 1024 };
        var client = new OllamaModelClient(new LocalAiOptions
        {
            ModelId = "test:local", ModelDigest = new('a', 64), RuntimeVersion = "test-version",
            CpuThreads = threads, KeepAliveSeconds = 45, MaxModelMemoryMb = memoryBudgetMb
        }) { HandlerFactory = () => handler };
        if (allowed)
        {
            await client.CompleteAsync(Bundle(), default);
            Assert.Equal(threads == 0 ? null : threads, handler.NumThread);
            Assert.Equal("45s", handler.KeepAlive);
        }
        else
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(Bundle(), default));
            Assert.Contains("over the 4000 MB memory budget", refused.Message);
            Assert.DoesNotContain("/api/chat", handler.Paths);
        }
    }

    private static EvidenceBundle Bundle() => new(1, "ExplainBlockers", DateTimeOffset.UtcNow, "v1", "RUN-1", null, null, null,
        [new("E1", "Quantity", "count", JsonSerializer.SerializeToElement(12), "parts", new("productionRun", "RUN-1"), "1", null, null, "RECORDED", "12 recorded parts")],
        [new("count", "12 recorded parts", ["E1"])], []);
    private sealed class RuntimeHandler(string behavior) : HttpMessageHandler
    {
        internal List<string> Paths = [];
        internal long? ModelSizeBytes;
        internal int? NumThread;
        internal string? KeepAlive;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            var path = request.RequestUri.AbsolutePath; Paths.Add(path);
            if (behavior == "redirect") return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://outside.invalid/") } };
            var body = path switch
            {
                "/api/version" => JsonSerializer.Serialize(new { version = behavior == "wrong_runtime" ? "other" : "test-version" }),
                "/api/tags" => behavior == "remote" ? "{\"models\":[{\"name\":\"test:local\",\"digest\":\"" + new string('a', 64) + "\",\"remote_host\":\"outside\"}]}"
                    : JsonSerializer.Serialize(new { models = new[] { new { name = "test:local", digest = new string(behavior == "wrong_digest" ? 'b' : 'a', 64), size = ModelSizeBytes } } }),
                _ => ""
            };
            if (path == "/api/chat")
            {
                using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.False(input.RootElement.GetProperty("stream").GetBoolean());
                Assert.False(input.RootElement.GetProperty("think").GetBoolean());
                Assert.False(input.RootElement.TryGetProperty("tools", out _));
                NumThread = input.RootElement.GetProperty("options").TryGetProperty("num_thread", out var thread) ? thread.GetInt32() : null;
                KeepAlive = input.RootElement.GetProperty("keep_alive").GetString();
                body = behavior == "oversize" ? new string('x', 262145) : JsonSerializer.Serialize(new
                {
                    done = true, done_reason = "stop", model = "test:local",
                    message = new { content = "{\"claimOrder\":[0]}", tool_calls = behavior == "tool_call" ? new[] { "shell" } : Array.Empty<string>() },
                    prompt_eval_count = behavior == "too_many_tokens" ? 9000 : 100, eval_count = 8, total_duration = 1000
                });
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
