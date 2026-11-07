using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PaperTrading.Api.Contracts;
using PaperTrading.Api.Domain;
using PaperTrading.Api.MarketData;

namespace PaperTrading.Tests;

public class ApiFlowTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<(HttpClient Client, string Token)> SignUp(string email = "a@example.com")
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "correct-horse", "Test"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(Json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);
        return (client, auth.Token);
    }

    [Fact]
    public async Task Protected_routes_require_a_token()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/portfolio")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/market/info")).StatusCode);
    }

    [Fact]
    public async Task Register_then_login_and_duplicates_and_wrong_passwords_are_refused()
    {
        await SignUp("Mixed@Example.com");
        var client = _factory.CreateClient();

        var dup = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("mixed@example.com", "another-pass", "X"));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var bad = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("mixed@example.com", "wrong-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        var ok = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("MIXED@example.com", "correct-horse"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var weak = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("b@example.com", "short", "X"));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
    }

    [Fact]
    public async Task Full_trade_flow_over_http_with_timestamps_marked_utc()
    {
        var (client, _) = await SignUp();
        _factory.Market.Set("AAA", 100m);

        var placed = await client.PostAsJsonAsync("/api/orders", new { symbol = "aaa", side = "Buy", type = "Market", quantity = 10 });
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var body = await placed.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Filled\"", body);

        var portfolio = await client.GetFromJsonAsync<PortfolioDto>("/api/portfolio", Json);
        Assert.Equal(99_000m, portfolio!.Cash);
        Assert.Equal("AAA", portfolio.Positions[0].Symbol);

        var list = await client.GetStringAsync("/api/orders");
        Assert.Matches("\"createdAt\":\"[^\"]+Z\"", list);
    }

    [Fact]
    public async Task Resting_limit_order_is_filled_by_the_background_loop_and_pushed_over_signalr()
    {
        var (client, token) = await SignUp();
        _factory.Market.Set("AAA", 100m);

        var server = _factory.Server;
        var hub = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/market", o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        var filled = new TaskCompletionSource<OrderDto>();
        var quoted = new TaskCompletionSource<Quote>();
        hub.On<OrderDto>("orderUpdated", o => filled.TrySetResult(o));
        hub.On<Quote>("quote", q => quoted.TrySetResult(q));
        await hub.StartAsync();
        await hub.InvokeAsync("Subscribe", new[] { "aaa" });

        Assert.Equal("AAA", (await quoted.Task.WaitAsync(TimeSpan.FromSeconds(10))).Symbol);

        var placed = await client.PostAsJsonAsync("/api/orders", new { symbol = "AAA", side = "Buy", type = "Limit", quantity = 5, limitPrice = 90 });
        Assert.Contains("\"status\":\"Open\"", await placed.Content.ReadAsStringAsync());

        _factory.Market.Set("AAA", 80m);
        var order = await filled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(80m, order.FilledPrice);
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task One_user_cannot_see_or_cancel_another_users_orders()
    {
        var (alice, _) = await SignUp("alice@example.com");
        var (bob, _) = await SignUp("bob@example.com");
        _factory.Market.Set("AAA", 100m);

        var placed = await alice.PostAsJsonAsync("/api/orders", new { symbol = "AAA", side = "Buy", type = "Limit", quantity = 1, limitPrice = 50 });
        var order = await placed.Content.ReadFromJsonAsync<OrderDto>(Json);

        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/orders/{order!.Id}")).StatusCode);
        Assert.Equal("[]", await bob.GetStringAsync("/api/orders"));
        Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync($"/api/orders/{order.Id}")).StatusCode);
    }

    [Fact]
    public async Task Errors_are_problem_details_and_a_dead_feed_is_503()
    {
        var (client, _) = await SignUp();
        _factory.Market.Set("AAA", 100m);

        var tooBig = await client.PostAsJsonAsync("/api/orders", new { symbol = "AAA", side = "Buy", type = "Market", quantity = 100_000 });
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
        Assert.Contains("buying power", await tooBig.Content.ReadAsStringAsync());

        _factory.Market.Failing = true;
        var down = await client.PostAsJsonAsync("/api/orders", new { symbol = "AAA", side = "Buy", type = "Market", quantity = 1 });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/nope")).StatusCode);
    }

    [Fact]
    public async Task New_accounts_get_a_default_watchlist_they_can_edit()
    {
        var (client, _) = await SignUp();
        _factory.Market.Set("AAA", 100m);

        var lists = await client.GetFromJsonAsync<WatchListDto[]>("/api/watchlists", Json);
        var main = Assert.Single(lists!);
        Assert.True(main.IsDefault);
        Assert.Contains("AAPL", main.Symbols);
        var url = $"/api/watchlists/{main.Id}";

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"{url}/symbols", new { symbol = "aaa" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"{url}/symbols", new { symbol = "AAA" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{url}/symbols", new { symbol = "NOPE" })).StatusCode);
        Assert.Contains("AAA\"", await client.GetStringAsync("/api/watchlists"));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{url}/symbols/AAA")).StatusCode);
        Assert.DoesNotContain("AAA\"", await client.GetStringAsync("/api/watchlists"));
    }

    [Fact]
    public async Task Named_lists_can_be_made_renamed_and_deleted_but_the_main_list_stays()
    {
        var (client, _) = await SignUp();
        var (other, _) = await SignUp("other@example.com");
        _factory.Market.Set("AAA", 100m);

        var created = await client.PostAsJsonAsync("/api/watchlists", new { name = "Semiconductors" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var list = await created.Content.ReadFromJsonAsync<WatchListDto>(Json);

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/watchlists", new { name = "Semiconductors" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/watchlists/{list!.Id}/symbols", new { symbol = "AAA" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/watchlists/{list.Id}", new { name = "Chips" })).StatusCode);

        // Another account can't touch it.
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/watchlists/{list.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/watchlists/{list.Id}/symbols", new { symbol = "AAA" })).StatusCode);

        var lists = await client.GetFromJsonAsync<WatchListDto[]>("/api/watchlists", Json);
        Assert.Equal(new[] { "Favorites", "Chips" }, lists!.Select(l => l.Name));
        Assert.Equal(new[] { "AAA" }, lists[1].Symbols);

        var main = lists[0];
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/watchlists/{main.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/watchlists/{list.Id}")).StatusCode);
    }
}
