using System.Text.RegularExpressions;

namespace PaperTrading.Api.Domain;

public static partial class Symbols
{
    [GeneratedRegex(@"^\^?[A-Z0-9][A-Z0-9.\-=]{0,14}$")]
    private static partial Regex Pattern();

    /// <summary>Uppercases a ticker and rejects anything that is not ticker-shaped.</summary>
    public static string Normalize(string? symbol)
    {
        var normalized = (symbol ?? "").Trim().ToUpperInvariant();
        if (!Pattern().IsMatch(normalized))
            throw new TradingException($"'{symbol}' is not a valid symbol.");
        return normalized;
    }

    public static bool TryNormalize(string? symbol, out string normalized)
    {
        normalized = (symbol ?? "").Trim().ToUpperInvariant();
        return Pattern().IsMatch(normalized);
    }
}
