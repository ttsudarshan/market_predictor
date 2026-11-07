using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Options;

namespace PaperTrading.Api.Insights;

public record LlmResult(string Text, long InputTokens, long OutputTokens);

public interface ILlmClient
{
    /// <summary>False when no API credentials exist, which parks videos instead of failing them.</summary>
    bool IsConfigured { get; }
    string Model { get; }

    /// <summary>Asks for JSON matching the schema.</summary>
    /// <exception cref="LlmUnavailableException">Worth retrying later.</exception>
    /// <exception cref="LlmRejectedException">Retrying the same request won't help.</exception>
    Task<LlmResult> CompleteJsonAsync(string system, string user, object schema, CancellationToken ct = default);
}

public class LlmUnavailableException(string message, Exception? inner = null, bool credentials = false, TimeSpan? retryAfter = null) : Exception(message, inner)
{
    /// <summary>The key was refused, so everything waits until it's fixed.</summary>
    public bool IsCredentialProblem { get; } = credentials;

    /// <summary>How long the provider asked for, if it said.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <param name="retryable">False when the same request is certain to fail the same way, so retrying only costs money.</param>
public class LlmRejectedException(string message, Exception? inner = null, bool retryable = true) : Exception(message, inner)
{
    public bool IsRetryable { get; } = retryable;
}

public sealed class AnthropicLlmClient(IOptions<LlmOptions> options) : ILlmClient
{
    private const int MaxTokens = 16000;
    private readonly Lazy<AnthropicClient> _client = new(() => Create(options.Value));

    public string Model => options.Value.Model;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.Value.ApiKey) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN")) ||
        Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "anthropic"));

    public async Task<LlmResult> CompleteJsonAsync(string system, string user, object schema, CancellationToken ct = default)
    {
        var schemaElement = JsonSerializer.SerializeToElement(schema);
        var parameters = new MessageCreateParams
        {
            Model = Model,
            MaxTokens = MaxTokens,
            Betas = ["server-side-fallback-2026-07-01"],
            // If a safety classifier declines, the API reruns the request on the model it recommends instead of failing it.
            Fallbacks = new BetaFallbacksParam(new Default()),
            System = system,
            OutputConfig = new BetaOutputConfig
            {
                Effort = options.Value.Effort,
                Format = new BetaJsonOutputFormat
                {
                    Schema = schemaElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value),
                },
            },
            Messages = [new BetaUserMessageParam { Content = user }],
        };

        BetaMessage response;
        try
        {
            response = await _client.Value.Beta.Messages.Create(parameters, ct);
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new LlmUnavailableException("Anthropic rejected the API key.", ex, credentials: true);
        }
        catch (AnthropicForbiddenException ex)
        {
            throw new LlmUnavailableException("This API key isn't allowed to use that model.", ex, credentials: true);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new LlmUnavailableException("Anthropic is rate-limiting requests.", ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new LlmUnavailableException("Anthropic had a server error.", ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new LlmUnavailableException("Anthropic could not be reached.", ex);
        }
        catch (AnthropicApiException ex)
        {
            throw new LlmRejectedException($"Anthropic refused the request: {ex.Message}", ex);
        }

        // A refusal is a normal 200 response, so this comes before reading any content.
        var stop = response.StopReason?.Raw();
        if (stop == "refusal") throw new LlmRejectedException("The model declined to process this.");
        if (stop == "max_tokens") throw new LlmRejectedException("The response was too long and was cut off.", retryable: false);

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
        return new LlmResult(text, response.Usage.InputTokens, response.Usage.OutputTokens);
    }

    private static AnthropicClient Create(LlmOptions o)
    {
        var client = new AnthropicClient();
        if (!string.IsNullOrWhiteSpace(o.ApiKey)) client = new AnthropicClient { ApiKey = o.ApiKey };
        if (!string.IsNullOrWhiteSpace(o.BaseUrl)) client = new AnthropicClient { ApiKey = o.ApiKey ?? client.ApiKey, BaseUrl = o.BaseUrl };
        return client;
    }
}
