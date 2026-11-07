using System.ComponentModel.DataAnnotations;
using PaperTrading.Api.Domain;

namespace PaperTrading.Api.Contracts;

public record RegisterRequest(
    [Required, EmailAddress, MaxLength(200)] string Email,
    [Required, MinLength(8), MaxLength(200)] string Password,
    [Required, MinLength(1), MaxLength(60)] string DisplayName);

public record LoginRequest([Required] string Email, [Required] string Password);

public record UserDto(Guid Id, string Email, string DisplayName);

public record AuthResponse(string Token, DateTime ExpiresAt, UserDto User);

public record PlaceOrderRequest(
    [Required] string Symbol,
    OrderSide Side,
    OrderType Type,
    decimal Quantity,
    decimal? LimitPrice,
    decimal? StopPrice,
    Guid? ClaimId = null);

public record OrderDto(
    Guid Id, string Symbol, OrderSide Side, OrderType Type, decimal Quantity,
    decimal? LimitPrice, decimal? StopPrice, OrderStatus Status, decimal? FilledPrice,
    string? RejectReason, DateTime CreatedAt, DateTime? ClosedAt, Guid? ClaimId)
{
    public static OrderDto From(Order o) => new(
        o.Id, o.Symbol, o.Side, o.Type, o.Quantity, o.LimitPrice, o.StopPrice,
        o.Status, o.FilledPrice, o.RejectReason, o.CreatedAt, o.ClosedAt, o.ClaimId);
}

public record FillDto(
    Guid Id, Guid OrderId, string Symbol, OrderSide Side, decimal Quantity, decimal Price,
    decimal Commission, decimal? RealizedPnl, DateTime ExecutedAt)
{
    public static FillDto From(Fill f) => new(
        f.Id, f.OrderId, f.Symbol, f.Side, f.Quantity, f.Price, f.Commission, f.RealizedPnl, f.ExecutedAt);
}

public record PositionDto(
    string Symbol, string Name, decimal Quantity, decimal AverageCost, decimal LastPrice, decimal DayBasePrice,
    decimal MarketValue, decimal CostBasis, decimal UnrealizedPnl, decimal UnrealizedPnlPercent,
    decimal DayChange, bool PriceUnavailable);

public record PortfolioDto(
    decimal Cash, decimal ReservedCash, decimal BuyingPower, decimal PositionsValue, decimal Equity,
    decimal StartingBalance, decimal TotalReturn, decimal TotalReturnPercent, decimal DayChange,
    decimal UnrealizedPnl, decimal RealizedPnl, decimal TotalCommissions,
    int ClosedTrades, decimal? WinRate, IReadOnlyList<PositionDto> Positions);

public record EquityPointDto(DateTime Timestamp, decimal Equity);

public record WatchListDto(Guid Id, string Name, bool IsDefault, IReadOnlyList<string> Symbols);

public record WatchListNameRequest([Required, MinLength(1), MaxLength(40)] string Name);

public record WatchlistAddRequest([Required] string Symbol);

public record ResetAccountRequest(decimal StartingBalance);

public record MarketInfoDto(
    string Provider, bool Simulated, string BaseCurrency, decimal DefaultStartingBalance,
    decimal CommissionPerOrder, int PollSeconds, bool Streaming);
