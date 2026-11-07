using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using SecurityClaim = System.Security.Claims.Claim;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PaperTrading.Api.Contracts;
using PaperTrading.Api.Data;
using PaperTrading.Api.Domain;

namespace PaperTrading.Api.Services;

public class JwtSettings
{
    public const string Issuer = "PaperTrading";
    public const string Audience = "PaperTrading";
    public string Key { get; set; } = "";
    public int ExpiryDays { get; set; } = 7;
}

public sealed class AccountService(
    AppDbContext db, ExecutionGate gate, IOptions<TradingOptions> trading, IOptions<JwtSettings> jwt)
{
    public const string DefaultListName = "Favorites";
    private static readonly string[] DefaultWatchlist = ["AAPL", "MSFT", "NVDA", "TSLA", "SPY"];
    private const decimal MinBalance = 1_000m, MaxBalance = 100_000_000m;

    private readonly PasswordHasher<User> _hasher = new();

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            throw new TradingException("An account with this email already exists.", StatusCodes.Status409Conflict);

        var balance = trading.Value.DefaultStartingBalance;
        var user = new User { Email = email, DisplayName = request.DisplayName.Trim(), Cash = balance, StartingBalance = balance };
        user.PasswordHash = _hasher.HashPassword(user, request.Password);

        db.Users.Add(user);
        db.WatchLists.Add(new WatchList { Id = user.Id, UserId = user.Id, Name = DefaultListName });
        db.WatchlistItems.AddRange(DefaultWatchlist.Select(s => new WatchlistItem { WatchListId = user.Id, Symbol = s }));
        db.Snapshots.Add(new PortfolioSnapshot { UserId = user.Id, Equity = balance });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent registration for the same email.
            throw new TradingException("An account with this email already exists.", StatusCodes.Status409Conflict);
        }
        return CreateToken(user);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        var result = user is null
            ? PasswordVerificationResult.Failed
            : _hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
            throw new TradingException("Incorrect email or password.", StatusCodes.Status401Unauthorized);

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user!.PasswordHash = _hasher.HashPassword(user, request.Password);
            await db.SaveChangesAsync(ct);
        }
        return CreateToken(user!);
    }

    public async Task<UserDto> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new TradingException("Account not found.", StatusCodes.Status401Unauthorized);
        return new UserDto(user.Id, user.Email, user.DisplayName);
    }

    /// <summary>Wipes trading history and restarts the account. The watchlist is kept.</summary>
    public async Task ResetAsync(Guid userId, decimal startingBalance, CancellationToken ct = default)
    {
        if (startingBalance is < MinBalance or > MaxBalance)
            throw new TradingException($"Starting balance must be between {MinBalance:N0} and {MaxBalance:N0}.");

        using var _ = await gate.EnterAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new TradingException("Account not found.", StatusCodes.Status401Unauthorized);

        await db.Orders.Where(o => o.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Fills.Where(f => f.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Positions.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Snapshots.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);

        user.Cash = startingBalance;
        user.StartingBalance = startingBalance;
        db.Snapshots.Add(new PortfolioSnapshot { UserId = userId, Equity = startingBalance });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private AuthResponse CreateToken(User user)
    {
        var expires = DateTime.UtcNow.AddDays(jwt.Value.ExpiryDays);
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Value.Key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            JwtSettings.Issuer, JwtSettings.Audience,
            [new SecurityClaim(ClaimTypes.NameIdentifier, user.Id.ToString())],
            expires: expires, signingCredentials: credentials);

        return new AuthResponse(
            new JwtSecurityTokenHandler().WriteToken(token), expires,
            new UserDto(user.Id, user.Email, user.DisplayName));
    }
}
