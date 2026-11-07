using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PaperTrading.Api.Domain;

namespace PaperTrading.Api.Data;

// SQLite stores decimals as TEXT, so Sum/Min/OrderBy on money columns cannot be translated to SQL.
// Money is always aggregated in memory; per-user row counts are small enough for this to be cheap.
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<Fill> Fills => Set<Fill>();
    public DbSet<WatchList> WatchLists => Set<WatchList>();
    public DbSet<WatchlistItem> WatchlistItems => Set<WatchlistItem>();
    public DbSet<PortfolioSnapshot> Snapshots => Set<PortfolioSnapshot>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<ChannelFollow> ChannelFollows => Set<ChannelFollow>();
    public DbSet<Video> Videos => Set<Video>();
    public DbSet<Transcript> Transcripts => Set<Transcript>();
    public DbSet<Claim> Claims => Set<Claim>();
    public DbSet<PriceBar> PriceBars => Set<PriceBar>();
    public DbSet<DailyBrief> DailyBriefs => Set<DailyBrief>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite drops DateTime.Kind; without this, API timestamps serialize with no 'Z' and browsers read them as local.
        builder.Properties<DateTime>().HaveConversion<UtcConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Email).IsUnique();

        b.Entity<Order>(e =>
        {
            e.Property(o => o.Side).HasConversion<string>();
            e.Property(o => o.Type).HasConversion<string>();
            e.Property(o => o.Status).HasConversion<string>();
            e.HasIndex(o => new { o.UserId, o.CreatedAt });
            e.HasIndex(o => o.Status);
            e.HasOne<User>().WithMany().HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Position>(e =>
        {
            e.HasKey(p => new { p.UserId, p.Symbol });
            e.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Fill>(e =>
        {
            e.Property(f => f.Side).HasConversion<string>();
            e.HasIndex(f => new { f.UserId, f.ExecutedAt });
            e.HasOne<User>().WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<WatchList>(e =>
        {
            e.HasIndex(l => new { l.UserId, l.Name }).IsUnique();
            e.Ignore(l => l.IsDefault);
            e.HasOne<User>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<WatchlistItem>(e =>
        {
            e.HasKey(w => new { w.WatchListId, w.Symbol });
            e.HasOne<WatchList>().WithMany().HasForeignKey(w => w.WatchListId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Channel>().HasIndex(c => c.YouTubeChannelId).IsUnique();

        b.Entity<ChannelFollow>(e =>
        {
            e.HasKey(f => new { f.UserId, f.ChannelId });
            e.HasOne<User>().WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Channel>().WithMany().HasForeignKey(f => f.ChannelId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Video>(e =>
        {
            e.Property(v => v.Status).HasConversion<string>();
            e.HasIndex(v => v.YouTubeVideoId).IsUnique();
            e.HasIndex(v => new { v.Status, v.NextAttemptAt });
            e.HasIndex(v => v.ChannelId);
            e.HasOne<Channel>().WithMany().HasForeignKey(v => v.ChannelId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Transcript>(e =>
        {
            e.HasKey(t => t.VideoId);
            e.HasIndex(t => t.FetchedAt);
            e.HasOne<Video>().WithOne().HasForeignKey<Transcript>(t => t.VideoId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Claim>(e =>
        {
            e.Property(c => c.Direction).HasConversion<string>();
            e.Property(c => c.Confidence).HasConversion<string>();
            e.Property(c => c.Status).HasConversion<string>();
            e.Property(c => c.Review).HasConversion<string>();
            e.HasIndex(c => c.VideoId);
            e.HasIndex(c => new { c.ChannelId, c.MadeAt });
            e.HasIndex(c => new { c.Ticker, c.MadeAt });
            e.HasIndex(c => c.Status);
            e.HasOne<Video>().WithMany().HasForeignKey(c => c.VideoId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Channel>().WithMany().HasForeignKey(c => c.ChannelId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<PriceBar>().HasKey(p => new { p.Symbol, p.Date });
        b.Entity<DailyBrief>().HasKey(d => d.Date);

        b.Entity<PortfolioSnapshot>(e =>
        {
            e.HasIndex(s => new { s.UserId, s.Timestamp });
            e.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v == null ? v : v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime(),
        v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
}
