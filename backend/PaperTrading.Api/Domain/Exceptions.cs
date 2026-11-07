namespace PaperTrading.Api.Domain;

/// <summary>A request the user can correct. Becomes a 4xx response with the message shown as-is.</summary>
public class TradingException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>The price feed could not answer. Becomes a 503 so nothing executes on bad data.</summary>
public class MarketDataUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
