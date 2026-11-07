using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaperTrading.Api.Insights;

namespace PaperTrading.Tests;

public class GeminiLlmClientTests
{
    private const string SecretKey = "test-secret-key";

    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://gemini.example.test/") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static (GeminiLlmClient Client, Stub Http) Build(HttpStatusCode status, string body)
    {
        var http = new Stub(status, body);
        var options = Options.Create(new LlmOptions { GeminiApiKey = SecretKey, GeminiModel = "gemini-test" });
        return (new GeminiLlmClient(http, options, NullLogger<GeminiLlmClient>.Instance), http);
    }

    private static string Answer(string finish, params object[] parts) => JsonSerializer.Serialize(new
    {
        candidates = new[] { new { finishReason = finish, content = new { parts } } },
        usageMetadata = new { promptTokenCount = 100, candidatesTokenCount = 20, thoughtsTokenCount = 30 },
    });

    private static readonly object Schema = new { type = "object", properties = new { claims = new { type = "array" } } };

    [Fact]
    public async Task The_answer_excludes_thinking_and_counts_it_as_output()
    {
        var (client, _) = Build(HttpStatusCode.OK, Answer("STOP", new { text = "planning", thought = true }, new { text = """{"claims":[]}""" }));

        var result = await client.CompleteJsonAsync("sys", "user", Schema);

        Assert.Equal("""{"claims":[]}""", result.Text);
        Assert.Equal(100, result.InputTokens);
        Assert.Equal(50, result.OutputTokens);
    }

    [Fact]
    public async Task The_request_has_the_schema_and_keeps_the_key_out_of_the_url()
    {
        var (client, http) = Build(HttpStatusCode.OK, Answer("STOP", new { text = "{}" }));

        await client.CompleteJsonAsync("the system prompt", "the transcript", Schema);

        Assert.Equal("https://gemini.example.test/v1beta/models/gemini-test:generateContent", http.Request!.RequestUri!.ToString());
        Assert.DoesNotContain(SecretKey, http.Request.RequestUri.ToString());
        Assert.Equal(SecretKey, http.Request.Headers.GetValues("x-goog-api-key").Single());

        using var body = JsonDocument.Parse(http.RequestBody!);
        Assert.Equal("the system prompt", body.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        var config = body.RootElement.GetProperty("generationConfig");
        Assert.Equal("application/json", config.GetProperty("responseMimeType").GetString());
        Assert.Equal("object", config.GetProperty("responseJsonSchema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_rate_limit_pauses_for_as_long_as_gemini_asks_and_uses_no_attempt()
    {
        var error = """{"error":{"code":429,"message":"Quota exceeded","status":"RESOURCE_EXHAUSTED","details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"41.5s"}]}}""";
        var (client, _) = Build(HttpStatusCode.TooManyRequests, error);

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => client.CompleteJsonAsync("s", "u", Schema));

        Assert.False(ex.IsCredentialProblem);
        Assert.Equal(TimeSpan.FromSeconds(42), ex.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "bad")]
    [InlineData(HttpStatusCode.Forbidden, "denied")]
    [InlineData(HttpStatusCode.BadRequest, "API key not valid. Please pass a valid API key.")]
    [InlineData(HttpStatusCode.NotFound, "This model is no longer available")]
    public async Task A_bad_key_or_retired_model_pauses_everything(HttpStatusCode status, string message)
    {
        var (client, _) = Build(status, JsonSerializer.Serialize(new { error = new { message } }));
        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => client.CompleteJsonAsync("s", "u", Schema));
        Assert.True(ex.IsCredentialProblem);
        Assert.DoesNotContain(SecretKey, ex.Message);
    }

    [Fact]
    public async Task A_server_error_is_worth_retrying()
    {
        var (client, _) = Build(HttpStatusCode.ServiceUnavailable, "not json");
        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => client.CompleteJsonAsync("s", "u", Schema));
        Assert.False(ex.IsCredentialProblem);
    }

    [Fact]
    public async Task A_cut_off_response_is_not_retried()
    {
        var (client, _) = Build(HttpStatusCode.OK, Answer("MAX_TOKENS", new { text = "{" }));
        var ex = await Assert.ThrowsAsync<LlmRejectedException>(() => client.CompleteJsonAsync("s", "u", Schema));
        Assert.False(ex.IsRetryable);
    }

    [Theory]
    [InlineData("""{"promptFeedback":{"blockReason":"PROHIBITED_CONTENT"}}""")]
    [InlineData("""{"candidates":[{"finishReason":"SAFETY"}]}""")]
    [InlineData("""{"candidates":[{"finishReason":"RECITATION","content":{"parts":[{"text":"{}"}]}}]}""")]
    [InlineData("""{"candidates":[{"finishReason":"STOP","content":{"parts":[]}}]}""")]
    public async Task A_declined_or_empty_answer_is_rejected_rather_than_read(string body)
    {
        var (client, _) = Build(HttpStatusCode.OK, body);
        var ex = await Assert.ThrowsAsync<LlmRejectedException>(() => client.CompleteJsonAsync("s", "u", Schema));
        Assert.True(ex.IsRetryable);
    }

    [Fact]
    public void With_no_key_it_reports_not_configured_so_videos_wait()
    {
        var saved = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
        try
        {
            var client = new GeminiLlmClient(new Stub(HttpStatusCode.OK, "{}"), Options.Create(new LlmOptions()), NullLogger<GeminiLlmClient>.Instance);
            Assert.False(client.IsConfigured);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", saved);
        }
    }

    [Theory]
    [InlineData("gemini", "", "", true)]
    [InlineData("GEMINI", "", "", true)]
    [InlineData("anthropic", "", "g-key", false)]
    [InlineData("", "", "g-key", true)]
    [InlineData("", "a-key", "g-key", false)]
    [InlineData("", "", "", false)]
    public void The_provider_comes_from_the_setting_or_else_from_which_key_is_there(string provider, string anthropicKey, string geminiKey, bool expected)
    {
        var options = new LlmOptions { Provider = provider, ApiKey = anthropicKey, GeminiApiKey = geminiKey };
        var savedAnthropic = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var savedGemini = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
        try
        {
            Assert.Equal(expected, options.UsesGemini());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", savedAnthropic);
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", savedGemini);
        }
    }
}
