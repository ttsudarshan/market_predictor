using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PaperTrading.Api.Data;
using PaperTrading.Api.Hubs;
using PaperTrading.Api.Infrastructure;
using PaperTrading.Api.Insights;
using PaperTrading.Api.MarketData;
using PaperTrading.Api.Services;

DotEnv.Load();
var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var dataDirectory = Path.GetFullPath(config["DataDirectory"] ?? "data", builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataDirectory);

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dataDirectory, "papertrading.db")}"));

builder.Services.Configure<TradingOptions>(config.GetSection("Trading"));
builder.Services.Configure<MarketDataOptions>(config.GetSection("MarketData"));
builder.Services.AddMemoryCache();

var useSimulation = string.Equals(config["MarketData:Provider"], "Simulated", StringComparison.OrdinalIgnoreCase);
builder.Services.AddSingleton<IMarketClock>(useSimulation ? new AlwaysOpenClock() : new MarketClock(TimeProvider.System));

builder.Services.AddHttpClient(YahooMarketDataProvider.HttpClientName, http =>
{
    http.BaseAddress = new Uri("https://query1.finance.yahoo.com/");
    http.Timeout = TimeSpan.FromSeconds(10);
    http.DefaultRequestHeaders.UserAgent.ParseAdd(YahooLiveStream.UserAgent);
})
// Cookies are sent by hand, as only the batch quote endpoint uses them.
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false });
builder.Services.AddSingleton<YahooMarketDataProvider>();
builder.Services.AddSingleton<SimulatedMarketDataProvider>();
if (!useSimulation) builder.Services.AddHostedService<YahooLiveStream>();
builder.Services.AddSingleton<IMarketDataProvider>(sp => useSimulation
    ? sp.GetRequiredService<SimulatedMarketDataProvider>()
    : sp.GetRequiredService<YahooMarketDataProvider>());

builder.Services.AddSingleton<ExecutionGate>();
builder.Services.AddSingleton<SubscriptionRegistry>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<PortfolioService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddHostedService<MarketLoopService>();

builder.Services.Configure<InsightsOptions>(config.GetSection("Insights"));
builder.Services.Configure<LlmOptions>(config.GetSection("Llm"));
builder.Services.AddSingleton<IPriceHistory, YahooPriceHistory>();
builder.Services.AddSingleton<IYouTubeGateway, YouTubeGateway>();
builder.Services.AddHttpClient(GeminiLlmClient.HttpClientName, http =>
{
    http.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    // Reading a long transcript takes a while, and this isn't streamed.
    http.Timeout = TimeSpan.FromMinutes(3);
});
builder.Services.AddSingleton<AnthropicLlmClient>();
builder.Services.AddSingleton<GeminiLlmClient>();
builder.Services.AddSingleton<ILlmClient>(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LlmOptions>>().Value.UsesGemini()
    ? sp.GetRequiredService<GeminiLlmClient>()
    : sp.GetRequiredService<AnthropicLlmClient>());
builder.Services.AddSingleton<PipelineState>();
builder.Services.AddScoped<IClaimExtractor, ClaimExtractor>();
builder.Services.AddScoped<ChannelService>();
builder.Services.AddScoped<ClaimQueries>();
builder.Services.AddScoped<DigestService>();
builder.Services.AddScoped<PipelineSteps>();
builder.Services.AddScoped<ScoringService>();
builder.Services.AddHostedService<InsightsPipelineService>();

var jwtKey = JwtKeyStore.Resolve(config["Jwt:Key"], dataDirectory);
builder.Services.Configure<JwtSettings>(config.GetSection("Jwt"));
builder.Services.PostConfigure<JwtSettings>(s => s.Key = jwtKey);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = JwtSettings.Issuer,
        ValidAudience = JwtSettings.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ClockSkew = TimeSpan.FromMinutes(1),
    };
    // Browsers can't set headers on WebSocket requests, so SignalR sends the token in the query string.
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var token = context.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                context.Token = token;
            return Task.CompletedTask;
        },
    };
});
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));

    // These two spend money or YouTube goodwill, so they get tighter limits per account.
    static string Account(HttpContext c) => c.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous";
    o.AddPolicy("ask", context => RateLimitPartition.GetFixedWindowLimiter(
        Account(context), _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("channels", context => RateLimitPartition.GetFixedWindowLimiter(
        Account(context), _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    DatabaseInitializer.Migrate(
        scope.ServiceProvider.GetRequiredService<AppDbContext>(), dataDirectory,
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Database"));
}

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHub<MarketHub>("/hubs/market");

// Angular handles its own routes; unknown /api paths still return 404 rather than the app's HTML.
app.Map("/api/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
// The general fallback skips paths that look like files, which would catch tickers such as RELIANCE.NS.
app.MapFallbackToFile("trade/{symbol}", "index.html");

app.Run();

public partial class Program;
