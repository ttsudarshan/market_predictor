export type OrderSide = 'Buy' | 'Sell';
export type OrderType = 'Market' | 'Limit' | 'Stop';
export type OrderStatus = 'Open' | 'Filled' | 'Cancelled' | 'Rejected';
export type ChartRange = '1D' | '5D' | '1M' | '6M' | '1Y' | '5Y';

export interface User {
  id: string;
  email: string;
  displayName: string;
}

export interface AuthResponse {
  token: string;
  expiresAt: string;
  user: User;
}

export interface Quote {
  symbol: string;
  name: string;
  currency: string;
  exchange: string;
  instrumentType: string;
  price: number;
  previousClose: number;
  dayHigh: number | null;
  dayLow: number | null;
  volume: number | null;
  fiftyTwoWeekHigh: number | null;
  fiftyTwoWeekLow: number | null;
  isMarketOpen: boolean;
  asOf: string;
  /** When the price last traded. */
  priceTime: string | null;
  market: MarketStatus | null;
  /** The latest pre-market or after-hours trade. Shown beside the price, and never used to value or fill anything. */
  extendedPrice: number | null;
  extendedChangePercent: number | null;
  extendedSession: string | null;
  change: number;
  changePercent: number;
  isTradable: boolean;
}

export interface MarketMovers {
  indexes: Quote[];
  trending: Quote[];
  gainers: Quote[];
  losers: Quote[];
  mostActive: Quote[];
  loadedAt: string;
  stale: boolean;
}

export interface MarketStatus {
  phase: 'Open' | 'PreMarket' | 'AfterHours' | 'Overnight' | 'Weekend' | 'Holiday';
  isOpen: boolean;
  label: string;
  /** What happens next, already written in Eastern Time. */
  detail: string;
  nextOpen: string | null;
  closesAt: string | null;
}

export interface Candle {
  time: number;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
}

export interface SymbolSearchResult {
  symbol: string;
  name: string;
  exchange: string;
  type: string;
}

export interface Order {
  id: string;
  symbol: string;
  side: OrderSide;
  type: OrderType;
  quantity: number;
  limitPrice: number | null;
  stopPrice: number | null;
  status: OrderStatus;
  filledPrice: number | null;
  rejectReason: string | null;
  createdAt: string;
  closedAt: string | null;
  claimId: string | null;
}

export interface Fill {
  id: string;
  orderId: string;
  symbol: string;
  side: OrderSide;
  quantity: number;
  price: number;
  commission: number;
  realizedPnl: number | null;
  executedAt: string;
}

export interface Position {
  symbol: string;
  name: string;
  quantity: number;
  averageCost: number;
  lastPrice: number;
  /** Price today's change is measured from: the previous close, or the fill for shares bought today. */
  dayBasePrice: number;
  marketValue: number;
  costBasis: number;
  unrealizedPnl: number;
  unrealizedPnlPercent: number;
  dayChange: number;
  priceUnavailable: boolean;
}

export interface Portfolio {
  cash: number;
  reservedCash: number;
  buyingPower: number;
  positionsValue: number;
  equity: number;
  startingBalance: number;
  totalReturn: number;
  totalReturnPercent: number;
  dayChange: number;
  unrealizedPnl: number;
  realizedPnl: number;
  totalCommissions: number;
  closedTrades: number;
  winRate: number | null;
  positions: Position[];
}

export interface EquityPoint {
  timestamp: string;
  equity: number;
}

export interface MarketInfo {
  provider: string;
  simulated: boolean;
  baseCurrency: string;
  defaultStartingBalance: number;
  commissionPerOrder: number;
  pollSeconds: number;
  streaming: boolean;
}

export interface PlaceOrder {
  symbol: string;
  side: OrderSide;
  type: OrderType;
  quantity: number;
  limitPrice?: number | null;
  stopPrice?: number | null;
  claimId?: string | null;
}

export interface WatchList {
  id: string;
  name: string;
  isDefault: boolean;
  symbols: string[];
}

export type VideoStatus = 'Discovered' | 'TranscriptFetched' | 'Extracted' | 'Scored' | 'NoCaptions' | 'Failed';
export type ClaimDirection = 'Bullish' | 'Bearish';
export type ClaimStatus = 'Pending' | 'Final' | 'Unscorable';
export type ClaimReview = 'Correct' | 'Incorrect';

export interface CreatorStats {
  channelId: string;
  title: string;
  videos: number;
  videosProcessed: number;
  totalCalls: number;
  finalCalls: number;
  pendingCalls: number;
  hitRate: number | null;
  beatBenchmarkRate: number | null;
  averageReturn: number | null;
  averageExcessReturn: number | null;
  targetHitRate: number | null;
  rank: number | null;
  trustWeight: number;
}

export interface VideoCounts {
  total: number;
  waiting: number;
  transcriptFetched: number;
  extracted: number;
  scored: number;
  noCaptions: number;
  failed: number;
}

export interface Channel {
  id: string;
  youTubeChannelId: string;
  title: string;
  addedAt: string;
  lastPolledAt: string | null;
  lastPollError: string | null;
  following: boolean;
  counts: VideoCounts;
  stats: CreatorStats;
}

export interface Video {
  id: string;
  youTubeVideoId: string;
  title: string;
  publishedAt: string | null;
  status: VideoStatus;
  lastError: string | null;
  claimCount: number;
  rejectedClaims: number;
  nextAttemptAt: string | null;
}

export interface Claim {
  id: string;
  videoId: string;
  channelId: string;
  channelTitle: string;
  videoTitle: string;
  youTubeVideoId: string;
  ticker: string;
  direction: ClaimDirection;
  targetPrice: number | null;
  horizonDays: number;
  horizonStated: boolean;
  confidence: 'Low' | 'Medium' | 'High';
  quote: string;
  timestampSeconds: number;
  reasoning: string | null;
  speaker: string | null;
  quoteVerified: boolean;
  madeAt: string;
  resolvesAt: string;
  status: ClaimStatus;
  entryPrice: number | null;
  exitPrice: number | null;
  returnPercent: number | null;
  excessReturnPercent: number | null;
  targetHit: boolean | null;
  review: ClaimReview | null;
}

export interface Reversal {
  ticker: string;
  firstAt: string;
  firstDirection: ClaimDirection;
  secondAt: string;
  secondDirection: ClaimDirection;
  daysApart: number;
}

export interface ChannelDetail {
  channel: Channel;
  videos: Video[];
  claims: Claim[];
  reversals: Reversal[];
}

export interface PipelineStatus {
  llmConfigured: boolean;
  model: string;
  llmPausedReason: string | null;
  llmPausedUntil: string | null;
  youTubePausedReason: string | null;
  youTubePausedUntil: string | null;
  whisperConfigured: boolean;
  minCallsToRank: number;
  defaultHorizonDays: number;
  benchmark: string;
  inputTokens: number;
  outputTokens: number;
  running: boolean;
}

export interface Accuracy {
  reviewed: number;
  correct: number;
  incorrect: number;
  accuracy: number | null;
  totalClaims: number;
  unverifiedQuotes: number;
}

export interface SentimentRow {
  ticker: string;
  bullishCalls: number;
  bearishCalls: number;
  netSentiment: number;
  bullishCreators: string[];
  bearishCreators: string[];
  latestCallAt: string;
}

export interface HeadsUp {
  disclaimer: string;
  brief: string | null;
  briefDate: string | null;
  windowDays: number;
  tickers: SentimentRow[];
}

export interface InboxItem {
  claim: Claim;
  worthWatching: boolean;
  reasons: string[];
}

export interface InboxDay {
  date: string;
  items: InboxItem[];
}

export interface AskResult {
  answer: string;
  citations: Claim[];
  disclaimer: string;
}

export interface ClaimTrade {
  orderId: string;
  symbol: string;
  side: OrderSide;
  quantity: number;
  fillPrice: number | null;
  status: OrderStatus;
  currentPrice: number | null;
  profitLoss: number | null;
  claimId: string;
  channelTitle: string;
  direction: ClaimDirection;
  placedAt: string;
}
