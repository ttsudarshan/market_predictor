namespace PaperTrading.Api.Services;

public class TradingOptions
{
    /// <summary>Only instruments quoted in this currency can be traded. There is no FX conversion.</summary>
    public string BaseCurrency { get; set; } = "USD";
    public decimal DefaultStartingBalance { get; set; } = 100_000m;
    public decimal CommissionPerOrder { get; set; }
    public int SnapshotMinutes { get; set; } = 5;
}

/// <summary>
/// Serializes every change to balances and positions. This keeps two concurrent orders from
/// spending the same cash. It assumes one app instance, which matches a local SQLite file.
/// </summary>
public sealed class ExecutionGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        return new Releaser(_gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
