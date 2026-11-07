using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PaperTrading.Api.Domain;
using PaperTrading.Api.Insights;

namespace PaperTrading.Api.Infrastructure;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            TradingException e => (e.StatusCode, e.Message),
            MarketDataUnavailableException e => (StatusCodes.Status503ServiceUnavailable, $"Market data is unavailable right now. {e.Message}"),
            YouTubeBlockedException => (StatusCodes.Status503ServiceUnavailable, "YouTube is refusing requests from this machine right now. Try again in a while."),
            LlmUnavailableException e => (StatusCodes.Status503ServiceUnavailable, e.Message),
            LlmRejectedException e => (StatusCodes.Status502BadGateway, e.Message),
            _ => (StatusCodes.Status500InternalServerError, "Something went wrong on the server."),
        };
        if (status >= 500) log.LogError(exception, "Request failed: {Path}", context.Request.Path);

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title }, ct);
        return true;
    }
}
