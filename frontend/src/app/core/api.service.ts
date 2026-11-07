import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import {
  Accuracy, AskResult, Candle, Channel, ChannelDetail, ChartRange, Claim, ClaimReview, ClaimTrade, CreatorStats, EquityPoint,
  Fill, HeadsUp, InboxDay, MarketInfo, MarketMovers, MarketStatus, Order, PipelineStatus, PlaceOrder, Portfolio, Quote, SymbolSearchResult, WatchList,
} from './models';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private http = inject(HttpClient);

  marketInfo = () => this.http.get<MarketInfo>('/api/market/info');
  marketStatus = () => this.http.get<MarketStatus>('/api/market/status');
  movers = () => this.http.get<MarketMovers>('/api/market/movers');
  quote = (symbol: string) => this.http.get<Quote>(`/api/market/quote/${encodeURIComponent(symbol)}`);
  candles = (symbol: string, range: ChartRange) =>
    this.http.get<Candle[]>(`/api/market/candles/${encodeURIComponent(symbol)}`, { params: { range } });
  search = (q: string) => this.http.get<SymbolSearchResult[]>('/api/market/search', { params: { q } });

  portfolio = () => this.http.get<Portfolio>('/api/portfolio');
  equityHistory = () => this.http.get<EquityPoint[]>('/api/portfolio/history');
  fills = () => this.http.get<Fill[]>('/api/portfolio/fills');

  orders = (openOnly = false) => this.http.get<Order[]>('/api/orders', { params: { openOnly } });
  placeOrder = (order: PlaceOrder) => this.http.post<Order>('/api/orders', order);
  cancelOrder = (id: string) => this.http.delete<Order>(`/api/orders/${id}`);

  watchLists = () => this.http.get<WatchList[]>('/api/watchlists');
  createWatchList = (name: string) => this.http.post<WatchList>('/api/watchlists', { name });
  renameWatchList = (id: string, name: string) => this.http.put<void>(`/api/watchlists/${id}`, { name });
  deleteWatchList = (id: string) => this.http.delete<void>(`/api/watchlists/${id}`);
  addToWatchList = (id: string, symbol: string) => this.http.post<void>(`/api/watchlists/${id}/symbols`, { symbol });
  removeFromWatchList = (id: string, symbol: string) =>
    this.http.delete<void>(`/api/watchlists/${id}/symbols/${encodeURIComponent(symbol)}`);

  channels = () => this.http.get<Channel[]>('/api/channels');
  addChannel = (link: string, backfill: number) => this.http.post<Channel>('/api/channels', { link, backfill });
  channel = (id: string) => this.http.get<ChannelDetail>(`/api/channels/${id}`);
  followChannel = (id: string) => this.http.post<void>(`/api/channels/${id}/follow`, {});
  unfollowChannel = (id: string) => this.http.delete<void>(`/api/channels/${id}/follow`);
  retryChannel = (id: string) => this.http.post<{ retried: number }>(`/api/channels/${id}/retry`, {});

  claims = (ticker: string) => this.http.get<Claim[]>('/api/claims', { params: { ticker } });
  reviewClaim = (id: string, review: ClaimReview | null) => this.http.post<void>(`/api/claims/${id}/review`, { review });

  leaderboard = () => this.http.get<CreatorStats[]>('/api/insights/leaderboard');
  headsUp = (followedOnly: boolean) => this.http.get<HeadsUp>('/api/insights/heads-up', { params: { followedOnly } });
  inbox = (days: number) => this.http.get<InboxDay[]>('/api/insights/inbox', { params: { days } });
  ask = (question: string) => this.http.post<AskResult>('/api/insights/ask', { question });
  pipelineStatus = () => this.http.get<PipelineStatus>('/api/insights/status');
  startPipeline = () => this.http.post<void>('/api/insights/pipeline/start', {});
  stopPipeline = () => this.http.post<void>('/api/insights/pipeline/stop', {});
  accuracy = () => this.http.get<Accuracy>('/api/insights/accuracy');
  claimTrades = () => this.http.get<ClaimTrade[]>('/api/insights/claim-trades');

  resetAccount = (startingBalance: number) => this.http.post<void>('/api/account/reset', { startingBalance });
}

/** Turns an HTTP failure into a sentence that can be shown to the user. */
export function errorMessage(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    if (err.status === 0) return "Can't reach the server. Check that it's running.";
    if (err.status === 429) return 'Too many attempts. Wait a minute and try again.';
    const validation = err.error?.errors as Record<string, string[]> | undefined;
    const first = validation && Object.values(validation)[0]?.[0];
    if (first) return first;
    if (typeof err.error?.title === 'string') return err.error.title;
  }
  return 'Something went wrong. Try again.';
}
