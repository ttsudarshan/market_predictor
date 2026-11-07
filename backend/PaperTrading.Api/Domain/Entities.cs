namespace PaperTrading.Api.Domain;

public enum OrderSide { Buy, Sell }

public enum OrderType { Market, Limit, Stop }

public enum OrderStatus { Open, Filled, Cancelled, Rejected }

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public string PasswordHash { get; set; } = "";
    public decimal Cash { get; set; }
    public decimal StartingBalance { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string Symbol { get; set; }
    public OrderSide Side { get; set; }
    public OrderType Type { get; set; }
    public decimal Quantity { get; set; }
    public decimal? LimitPrice { get; set; }
    public decimal? StopPrice { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Open;
    public decimal? FilledPrice { get; set; }
    public string? RejectReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }

    /// <summary>The creator's prediction this trade followed, if any.</summary>
    public Guid? ClaimId { get; set; }
}

public class Position
{
    public Guid UserId { get; set; }
    public required string Symbol { get; set; }
    public decimal Quantity { get; set; }

    /// <summary>Average cost per share, including buy commissions.</summary>
    public decimal AverageCost { get; set; }
}

/// <summary>Immutable ledger entry written once per executed order.</summary>
public class Fill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Guid UserId { get; set; }
    public required string Symbol { get; set; }
    public OrderSide Side { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Commission { get; set; }

    /// <summary>Realized profit or loss, net of commission. Null on buys.</summary>
    public decimal? RealizedPnl { get; set; }
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
}

public class WatchlistItem
{
    public Guid WatchListId { get; set; }
    public required string Symbol { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public class PortfolioSnapshot
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public decimal Equity { get; set; }
}
