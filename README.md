# Paperdesk

Paper trading app with live prices, plus a leaderboard that tracks whether finance YouTubers' stock calls actually played out.

Paste a YouTube channel, it pulls the transcripts, extracts the stock predictions with an LLM, and scores each call against real prices (vs SPY).

**Stack:** ASP.NET Core 8, EF Core + SQLite, SignalR, Angular 22, TradingView Lightweight Charts

No real orders, no broker connection.

## Setup

Needs .NET 8 SDK and Node.js (22.22+ / 24.15+).

```bash
git clone https://github.com/ttsudarshan/market_predictor.git
cd market_predictor
cd frontend && npm install && npm run build && cd ..
cp .env.example .env
dotnet run --project backend/PaperTrading.Api
```

Open http://localhost:5080 and make an account. The database and JWT key are created automatically in `backend/PaperTrading.Api/data/`.

### API key

Only needed for the YouTube predictions part. Trading and charts work without one. Put one of these in `.env`:

```env
GEMINI_API_KEY=     # free tier at aistudio.google.com/apikey
ANTHROPIC_API_KEY=  # paid, roughly $4-9 per 60-video channel on the default model
```

### Market closed?

Orders only fill during NYSE hours. Use the simulated feed to test anytime:

```bash
dotnet run --project backend/PaperTrading.Api --launch-profile simulated
```

### Dev / tests

```bash
cd frontend && npm start   # http://localhost:4200, proxies to the API on 5080
dotnet test
```

## Features

- Market, limit and stop orders, fractional shares
- Portfolio with live P&L, return, win rate and value chart
- Candlestick charts with your orders and average cost overlaid
- Watchlists, order history, account reset
- Creator leaderboard, each call linked to the exact moment in the video
- Market sentiment from creators' recent calls, weighted by their track record
- Ask questions about the calls in plain English

## How scoring works

- Entry is the first close after the video was published, so no credit for moves that already happened
- Each call's return is compared to SPY over the same period
- No timeframe given → 30 days, counts half
- Repeated calls on the same ticker count once
- Quotes the model returns that aren't actually in the transcript are thrown out
- Creators need 10 finished calls to get ranked

## Config

Settings live in `backend/PaperTrading.Api/appsettings.json` and can be overridden with env vars (`Trading__BaseCurrency=INR`). Useful ones:

| Setting | Default |
| --- | --- |
| `MarketData:Provider` | `Yahoo` (or `Simulated`) |
| `Trading:DefaultStartingBalance` | `100000` |
| `Trading:BaseCurrency` | `USD` |
| `Llm:Provider` | auto (`Gemini` / `Anthropic`) |
| `Llm:Model` | `claude-opus-5-5` |
| `Llm:GeminiModel` | `gemini-3.5-flash` |
| `Insights:DefaultBackfill` | `60` videos |

## Caveats

- Uses unofficial YouTube and Yahoo endpoints. Fine for personal use, don't host it publicly.
- Single instance only (SQLite + in-process locking).
- Not financial advice.
