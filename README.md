# Paperdesk

![.NET 8](https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet)
![Angular](https://img.shields.io/badge/Angular-22-DD0031?logo=angular)
![SQLite](https://img.shields.io/badge/SQLite-EF%20Core-003B57?logo=sqlite)
![Status](https://img.shields.io/badge/status-personal%20project-lightgrey)

> Paper trading with live prices, plus a scoreboard that checks whether finance YouTubers' stock calls actually came true.

A stock tracker and paper trading site that also checks finance YouTubers' predictions. Paste a channel link, and it reads the channel's recent videos, pulls out the calls they made, scores each one against what the price actually did, and ranks creators by whether they beat the market.

- **Backend:** C# / ASP.NET Core 8, EF Core with SQLite, SignalR, JWT auth, YoutubeExplode, the Anthropic SDK
- **Frontend:** Angular 22, TradingView Lightweight Charts

No real orders are placed, and nothing here connects to a broker.

## Quick start

### 1. Install the prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js](https://nodejs.org/) in a version Angular 22 supports: `^22.22.3`, `^24.15.0` or `>=26.0.0`
- Git

### 2. Clone and build

```bash
git clone https://github.com/ttsudarshan/market_predictor.git
cd <repo-name>
cd frontend && npm install && npm run build && cd ..
```

The frontend build goes into the API's `wwwroot`, so a single server handles everything.

### 3. Add your own API key (optional)

Paper trading, charts and live prices work **without any key**. A key is only needed to read YouTube videos and extract predictions.

```bash
cp .env.example .env
```

Open `.env` and fill in **one** of these:

| Provider | Get a key | Cost |
| --- | --- | --- |
| Google Gemini | https://aistudio.google.com/apikey | Free tier available. Google may use content sent on the free tier to improve its products, and its limits change often |
| Anthropic Claude | https://console.anthropic.com/ | Billed by usage (see [cost](#cost-on-anthropic)) |

```env
GEMINI_API_KEY=your-key-here
# or
ANTHROPIC_API_KEY=your-key-here
```

`.env` is git-ignored, so your key stays on your machine. If you prefer, you can set the same names as normal environment variables instead. Those take priority over `.env`.

### 4. Run

```bash
dotnet run --project backend/PaperTrading.Api
```

Open http://localhost:5080 and create an account. On first run the app creates its database and a token-signing key in `backend/PaperTrading.Api/data/`. Nothing needs to be set up by hand.

To read predictions, open **Creators** and paste a channel link. `youtube.com/@name`, `@name`, a `/channel/` link or a link to one of its videos all work. Start with **Last 5 (trial)** to check that your key works.

Without a key, videos are still discovered and their transcripts saved. They're processed as soon as you add a key and restart. If Gemini's free-tier limit is hit, processing pauses without using up a video's attempts.

### Cost on Anthropic

A backfill of 60 videos at about 10,000 tokens each is about 600,000 input tokens. On the default model (`claude-opus-5-5`, $4 in and $20 out per million tokens), expect roughly **$4 to $9 per channel**, including output and the model's thinking. This is an estimate, not a measurement. Each video is only ever processed once, and adding a channel again with a larger number only processes the new videos. Setting `Llm__Model=claude-haiku-5-5` in `.env` costs about 40 times less, and may extract less accurately. The Gemini default model is `gemini-3.5-flash`. To change it, set `Llm__GeminiModel`.

### Try it when the market is closed

Real orders only fill in market hours (see [Trading rules](#trading-rules)). To practise at any other time, use the simulated feed, which is always open and moves every 2 seconds:

```bash
dotnet run --project backend/PaperTrading.Api --launch-profile simulated
```

The top bar always shows which feed is active.

### How prices stay current

Real prices come from Yahoo in two ways, and the newest trade always wins:

- **Live stream.** One connection to the same stream Yahoo's own pages use carries each trade for whatever's on someone's screen, plus anything with an open order. Screens and resting orders pick up new prices within a second. The stream reconnects by itself if it drops.
- **Batch check.** Every 15 seconds, all watched symbols are fetched in one request. This fills in what the stream doesn't carry, such as the previous close and volume, and keeps prices moving if the stream is down. If the batch is refused, quotes fall back to one request per symbol.

The Home lists refresh once a minute, shared by all accounts, so their order stays still between refreshes while their prices move. Pre-market and after-hours trades are shown beside the price, and never used to value a position or fill an order.

### Develop the frontend

Run the API as above, then in a second terminal run `cd frontend && npm start`, and open http://localhost:4200. The dev server reloads on save and proxies `/api` and `/hubs` to port 5080.

### Run the tests

```bash
dotnet test
```

## What it does

- **Orders:** market, limit and stop, with fractional shares down to 4 decimal places
- **Home:** the S&P 500, Nasdaq and Dow, the shares you hold, and Yahoo's trending, top gainers, top losers and most active lists
- **Portfolio:** the shares you hold, account value, today's change, total return, realized and unrealized P&L, win rate and a value-over-time chart, all updating live
- **Trading screen:** candlestick charts from 1 day to 5 years, with open orders and your average cost drawn on the chart
- **Lists:** named lists with live prices, plus a Favorites list that can't be deleted
- **Orders and history:** cancel open orders, and review every order and trade
- **Account reset:** start again with a balance of your choice
- **Creators:** paste a channel and watch its videos process live, with a leaderboard, each creator's calls with the exact quote and a link to that moment in the video, and a list of times they changed their mind
- **Digest:** market heads-up, questions in plain English, and an inbox of new calls with reasons they're worth watching
- **Paper trade a call:** one click opens the ticket on that call, and the portfolio tracks how trades from each creator did

## How predictions are processed

```
Discovered  ->  TranscriptFetched  ->  Extracted  ->  Scored
                (or NoCaptions)        (or Failed)
```

Each step saves its result and status together, so a restart picks up where it left off, and no video is fetched or sent to the model twice.

1. **Find videos.** A channel's last 30 or 60 uploads are queued, and it's checked for new ones every 30 minutes. This reads the uploads list, because YouTube's RSS feeds returned 404 when tested.
2. **Fetch transcripts,** one at a time with a random 3 to 8 second gap, preferring English captions. Missing captions are retried after 1, 4, 16 and 64 hours. If YouTube starts refusing requests, all fetching pauses for 30 minutes.
3. **Extract calls.** The model returns each call's ticker, direction, target, timeframe, confidence, exact quote and timestamp as structured JSON. Two checks don't rely on the model: a quote that isn't in the transcript is flagged and never counts, and a ticker Yahoo doesn't know is dropped.
4. **Score calls.** This is plain arithmetic on real Yahoo prices, even when the trading screens use the simulated feed.

### Scoring rules

| Rule | Behaviour |
| --- | --- |
| Entry | The first daily close at or after publication, so a call gets no credit for moves that had already happened. Each close uses that day's real New York closing time, so a video published at 4:30 PM ET gets the next day's close |
| Exit | The first close on or after the call's date. Until then it's Pending, with a provisional figure that doesn't count |
| No timeframe given | Scored over 30 days, marked low confidence, and counts half |
| Against the market | Each call's return is compared with SPY over the same dates. Bearish calls are sign-flipped |
| Targets | Hit if the day's high (or low, for a bearish call) reached it inside the window. A target already passed at entry is ignored |
| Repeats | Repeating a call while it's still open counts once, so 50 "SPY goes up" videos are one call |
| Ranking | By average return against SPY. A creator needs 10 finished calls to be ranked, and the sample size is always shown |
| Not counted | Calls with an unverified quote, calls you mark Wrong, and calls with no usable prices |

Returns exclude dividends. To measure extraction accuracy, open a creator, mark calls Correct or Wrong, and read the figure on the Creators page. That measures how many extracted calls were right, not how many real calls were missed.

### Market heads-up

This counts each creator's latest open call per ticker from the last 14 days, weighting creators with better records more. Small samples are pulled towards average, so a few lucky calls earn little extra weight. It shows sentiment, not a price forecast, and the page says so.

### Questions in plain English

Claims are short, so the relevant ones go straight into the prompt, narrowed by any ticker or creator named in the question. Vector search would only be worth adding at far larger scale. Citations are checked against the claims that were sent.

### Videos with no captions (optional)

```bash
python3 -m venv .venv && .venv/bin/pip install faster-whisper "av<16"
export Insights__WhisperCommand=".venv/bin/python tools/whisper_transcribe.py {audio}"
```

This runs on the final retry for a video with no captions. It uses the original audio track, not YouTube's auto-dubbed ones.

### Very long videos

A response that hits the 16,000 token limit is marked Failed straight away, as retrying would only repeat the cost. This is most likely on multi-hour livestreams with many calls.

## Trading rules

| Rule | Behaviour |
| --- | --- |
| Fill price | The last traded price, with no spread, slippage or partial fills |
| When orders fill | Only in the regular session: 9:30 AM to 4:00 PM New York time on NYSE trading days, or 1:00 PM on early-close days. The times follow daylight saving |
| Market closed | This covers pre-market, after hours, overnight, weekends and NYSE holidays. Market orders are refused. Limit and stop orders can be placed, and wait for the open, even if their price has already been met |
| At the open | Nothing fills until the feed shows a trade from today, so an order can't fill on yesterday's close. A resting order then fills at the first price that meets it, including a better one after a gap |
| No recent trade | If a stock's last trade is over 30 minutes old in market hours, it doesn't fill. This covers halts and a stuck feed, and also very thinly traded stocks |
| Simulated feed | Always open, as that's what it's for |
| Other exchanges | With a different `BaseCurrency`, tickers outside New York follow the hours Yahoo reports for their exchange |
| Limit orders | Buys fill at or below the limit and sells at or above it, at the last price. In market hours, a limit that can already fill does so immediately |
| Stop orders | Buys trigger at or above the stop and sells at or below it, then fill at the last price. A stop already past the market is refused |
| Open buy orders | Hold back cash at their limit or stop price, which lowers buying power |
| Open sell orders | Hold back the shares, so they can't be sold twice |
| Triggered without funds | A buy stop that gaps above your available cash is rejected, with the reason shown on the order |
| Instruments | Stocks and ETFs in the account currency, with no shorting, margin, dividends or splits |

## Configuration

Settings are in `backend/PaperTrading.Api/appsettings.json`. Any of them can be overridden with an environment variable, using `__` between levels, for example `Trading__BaseCurrency=INR`.

| Setting | Default | Meaning |
| --- | --- | --- |
| `MarketData:Provider` | `Yahoo` | `Yahoo` for real prices, `Simulated` for the random walk |
| `MarketData:YahooPollSeconds` | `15` | How often all watched prices get a full check. Prices also stream between checks |
| `MarketData:SimulatedPollSeconds` | `2` | How often simulated prices move |
| `MarketData:SimulatedVolatility` | `0.0012` | Size of each simulated move, as a fraction of the price |
| `MarketData:SeedSimulationFromYahoo` | `true` | Start simulated prices from real ones. Set to `false` to run fully offline, limited to a built-in list of tickers |
| `Trading:BaseCurrency` | `USD` | Account currency. Only tickers quoted in it can be traded, so use `INR` for NSE tickers like `RELIANCE.NS` |
| `Trading:DefaultStartingBalance` | `100000` | Balance for new accounts |
| `Trading:CommissionPerOrder` | `0` | Flat fee per executed order |
| `Trading:SnapshotMinutes` | `5` | How often account value is recorded for the chart |
| `Jwt:Key` | generated | Token signing key, at least 32 characters. If empty, one is generated and saved in `data/jwt.key` |
| `Jwt:ExpiryDays` | `7` | How long a sign-in lasts |
| `DataDirectory` | `data` | Where the SQLite database, its backups and the generated key are kept |
| `Llm:Model` | `claude-opus-5-5` | The Anthropic model that reads transcripts |
| `Llm:Effort` | `high` | `low` to `max`. Higher effort costs more |
| `Llm:Provider` | empty | `Gemini` or `Anthropic`. If empty, Gemini is used when only a Gemini key is set |
| `Llm:GeminiModel` | `gemini-3.5-flash` | The Gemini model that reads transcripts |
| `Llm:GeminiApiKey` | empty | The Gemini key, if `GEMINI_API_KEY` isn't set. Prefer `.env` |
| `Insights:DefaultBackfill` | `60` | Past videos to process if none is chosen |
| `Insights:PollMinutes` | `30` | How often channels are checked for new uploads |
| `Insights:MinFetchDelaySeconds`, `MaxFetchDelaySeconds` | `3`, `8` | Gap between transcript requests. Lowering it risks a block |
| `Insights:RetentionDays` | `90` | Transcripts are deleted after this. Calls, quotes and scores are kept for good |
| `Insights:DefaultHorizonDays` | `30` | Horizon for a call with no timeframe |
| `Insights:MinCallsToRank` | `10` | Finished calls needed to be ranked |
| `Insights:HeadsUpDays` | `14` | How far back the heads-up looks |
| `Insights:Benchmark` | `SPY` | What every call is compared with |
| `Insights:WhisperCommand` | empty | Speech-to-text command. See above |

`BaseCurrency` applies to the whole server, and existing accounts aren't converted if you change it.

## How it fits together

```
backend/PaperTrading.Api
  Domain/         Entities and error types
  Data/           EF Core context (the database is created on first run)
  MarketData/     IMarketDataProvider with Yahoo and simulated feeds, YahooLiveStream for prices as they trade,
                  IPriceHistory for real daily prices,
                  and MarketClock with the NYSE calendar, which decides when orders can fill
  Insights/       YouTube access, the Claude and Gemini clients, extraction, scoring, ranking, digest, the background pipeline
  Services/       OrderService (all money movement), PortfolioService, AccountService,
                  MarketLoopService (polls prices, fills resting orders, pushes updates)
  Hubs/           SignalR hub for live quotes and order updates
  Controllers/    REST API
backend/PaperTrading.Tests   Orders, scoring, pipeline, upgrades and API, with fake YouTube, model and market
tools/          Optional speech-to-text script
frontend/src/app
  core/           API client, auth, live connection
  shared/         Charts, symbol search, number formatting
  pages/          Home, portfolio, trade, lists, orders, creators, digest, account, sign in
```

Worth knowing if you change it:

- **Run one instance only.** Balance changes are serialized by an in-process lock (`ExecutionGate`), which is safe for one server and one SQLite file. Running several instances needs a real database and row-level concurrency control.
- **Money is `decimal`.** SQLite stores decimals as text, so it can't sum or sort them in SQL. Money is added up in C# after loading the rows.
- **Database changes use EF migrations.** After changing an entity, run `dotnet tool restore`, then `dotnet ef migrations add YourChange --project backend/PaperTrading.Api --output-dir Data/Migrations`. Pending migrations apply at startup, and a backup is saved to the data folder first.

## Limitations

- **Transcript fetching breaks YouTube's terms.** It reads YouTube's unofficial endpoints, which is fine for personal use, but don't host this publicly. It can also stop working whenever YouTube changes something, or block a cloud server's IP. For a demo, use a recording or a snapshot of processed data.
- **This doesn't predict the market.** It reports what creators said and how those calls turned out. Past accuracy doesn't guarantee future results, and nothing here is financial advice.
- **Some channels' uploads can't be read.** YouTube refused the uploads list for one channel in testing. The Creators page shows this under Problems.
- **Yahoo's endpoints are unofficial.** They need no key, but they aren't a supported API, may be delayed, and can change or rate-limit without notice. Check Yahoo's terms before using this beyond personal use. To use a licensed feed, implement `IMarketDataProvider` and register it in `Program.cs`.
- **The market calendar can't foresee unscheduled closures.** It follows the NYSE's rules for holidays and early closes, checked against its published 2026 to 2028 dates. If the exchange shuts unexpectedly, nothing fills, because no new trades arrive, although the status line still says open. Update `NyseCalendar` if the rules change.
- **Prices can differ from Robinhood's by a few cents.** Yahoo's real-time US price comes from Nasdaq's feed rather than every exchange, and Robinhood uses its own sources. Yahoo also reports indexes like the S&P 500 as delayed.
- **If Yahoo can't be reached,** the last price stays usable for 2 minutes. After that, new orders return an error rather than filling at an old price.
- **Before hosting it publicly,** put it behind HTTPS. It has no password reset or email verification, and the sign-in token is kept in browser `localStorage`.

## Security notes

- Never commit API keys. Put them in `.env` (git-ignored) or environment variables. Only `.env.example`, with empty values, is committed.
- The `data/` folder (SQLite database, backups, `jwt.key`) is git-ignored and is created on first run.

## Disclaimer

This project is for learning and personal use. It is not financial advice, places no real trades, and is not affiliated with YouTube, Yahoo, Google or Anthropic.
