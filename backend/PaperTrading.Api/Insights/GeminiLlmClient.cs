using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace PaperTrading.Api.Insights;

/// <summary>
/// Google's Gemini API, which has a free tier. Google has no official .NET SDK for it here, so this is plain HTTP.
/// The key goes in a header, never the URL, so it can't end up in a log.
/// </summary>
public sealed class GeminiLlmClient(IHttpClientFactory httpFactory, IOptions<LlmOptions> options, ILogger<GeminiLlmClient> log) : ILlmClient
{
    public const string HttpClientName = "gemini";

    /// <summary>Includes the model's thinking, which Gemini counts as output.</summary>
    private const int MaxOutputTokens = 32000;
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string? ApiKey => string.IsNullOrWhiteSpace(options.Value.GeminiApiKey)
        ? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
        : options.Value.GeminiApiKey;

    public string Model => options.Value.GeminiModel;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<LlmResult> CompleteJsonAsync(string system, string user, object schema, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{Uri.EscapeDataString(Model)}:generateContent")
        {
            Content = JsonContent.Create(new
            {
                systemInstruction = new { parts = new[] { new { text = system } } },
                contents = new[] { new { role = "user", parts = new[] { new { text = user } } } },
                generationConfig = new
                {
                    responseMimeType = "application/json",
                    responseJsonSchema = schema,
                    maxOutputTokens = MaxOutputTokens,
                },
            }),
        };
        request.Headers.Add("x-goog-api-key", ApiKey);

        try
        {
            using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw ToException(response.StatusCode, body);
            return ReadResult(body);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmUnavailableException("Gemini could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmUnavailableException("Gemini took too long to answer.", ex);
        }
    }

    private LlmResult ReadResult(string body)
    {
        var root = JsonNode.Parse(body);
        if (root?["promptFeedback"]?["blockReason"]?.GetValue<string>() is { } blocked)
            throw new LlmRejectedException($"Gemini declined to process this ({blocked}).");

        var candidate = root?["candidates"]?[0];
        var finish = candidate?["finishReason"]?.GetValue<string>();
        if (finish == "MAX_TOKENS") throw new LlmRejectedException("The response was too long and was cut off.", retryable: false);
        if (finish is not (null or "STOP")) throw new LlmRejectedException($"Gemini declined to process this ({finish}).");

        // Thinking comes back as parts too, and isn't part of the answer.
        var text = string.Concat((candidate?["content"]?["parts"]?.AsArray() ?? [])
            .Where(p => p?["thought"]?.GetValue<bool>() != true)
            .Select(p => p?["text"]?.GetValue<string>()));
        if (string.IsNullOrWhiteSpace(text)) throw new LlmRejectedException("Gemini returned an empty response.");

        var usage = root?["usageMetadata"];
        var output = (usage?["candidatesTokenCount"]?.GetValue<long>() ?? 0) + (usage?["thoughtsTokenCount"]?.GetValue<long>() ?? 0);
        return new LlmResult(text, usage?["promptTokenCount"]?.GetValue<long>() ?? 0, output);
    }

    private Exception ToException(HttpStatusCode status, string body)
    {
        var error = ReadError(body);
        log.LogWarning("Gemini answered {Status}: {Message}", (int)status, error.Message);

        return status switch
        {
            HttpStatusCode.TooManyRequests => new LlmUnavailableException(
                "Gemini's usage limit was reached, so this will carry on when it allows more.", retryAfter: error.RetryAfter),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new LlmUnavailableException("Gemini rejected the API key.", credentials: true),
            HttpStatusCode.BadRequest when error.Message.Contains("API key", StringComparison.OrdinalIgnoreCase) =>
                new LlmUnavailableException("Gemini rejected the API key.", credentials: true),
            // A retired model needs a settings change, and every request would fail the same way until then.
            HttpStatusCode.NotFound => new LlmUnavailableException($"Gemini doesn't offer the model {Model}. Change Llm:GeminiModel.", credentials: true),
            >= HttpStatusCode.InternalServerError => new LlmUnavailableException("Gemini had a server error."),
            _ => new LlmRejectedException($"Gemini refused the request: {error.Message}"),
        };
    }

    private static (string Message, TimeSpan? RetryAfter) ReadError(string body)
    {
        try
        {
            var error = JsonNode.Parse(body)?["error"];
            var message = error?["message"]?.GetValue<string>() ?? "no details given";
            TimeSpan? wait = null;
            foreach (var detail in error?["details"]?.AsArray() ?? [])
            {
                if (detail?["retryDelay"]?.GetValue<string>() is { } delay && delay.EndsWith('s') &&
                    double.TryParse(delay[..^1], System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                {
                    var asked = TimeSpan.FromSeconds(Math.Ceiling(seconds));
                    wait = asked > MaxWait ? MaxWait : asked;
                }
            }
            return (message, wait);
        }
        catch (JsonException)
        {
            return ("an unreadable error", null);
        }
    }
}
