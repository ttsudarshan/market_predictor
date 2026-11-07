import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { catchError, combineLatest, map, of, startWith, switchMap } from 'rxjs';
import { ApiService, errorMessage } from '../core/api.service';
import { LiveService } from '../core/live.service';
import { MarketInfoStore } from '../core/market-info.store';
import { Candle, ChartRange, Claim, Order, OrderSide, OrderType, Portfolio, Quote, WatchList } from '../core/models';
import { ToastService } from '../core/toast.service';
import { ClaimCardComponent } from '../shared/claim-card.component';
import { FlashDirective } from '../shared/flash.directive';
import { FORMAT_PIPES, formatMarketTime } from '../shared/format';
import { ChartLine, PriceChartComponent } from '../shared/price-chart.component';

type LoadState = 'loading' | 'ready' | 'not-found' | 'failed';

@Component({
  selector: 'app-trade-page',
  imports: [RouterLink, PriceChartComponent, FlashDirective, ClaimCardComponent, ...FORMAT_PIPES],
  template: `
    @switch (state()) {
      @case ('not-found') {
        <div class="empty page-empty">
          <h1 class="mono">{{ symbol() }}</h1>
          <p>We couldn't find this ticker. Check the spelling, or search for the company name above.</p>
          <a routerLink="/portfolio">Back to portfolio</a>
        </div>
      }
      @case ('failed') {
        <div class="notice notice-bad" role="alert">{{ loadError() }}</div>
      }
      @case ('loading') {
        <p class="muted loading">Loading {{ symbol() }}…</p>
      }
      @default {
        @if (quote(); as q) {
          <header class="quote-head">
            <div>
              <h1>
                <span class="mono">{{ q.symbol }}</span>
                <button type="button" class="ghost watch-toggle" [attr.aria-pressed]="favorite()" (click)="toggleList(mainList())">
                  {{ favorite() ? '★ Favorite' : '☆ Favorite' }}
                </button>
                @if (lists().length > 1) {
                  <details class="list-menu">
                    <summary class="ghost">Lists</summary>
                    <div class="list-menu-body">
                      @for (l of lists(); track l.id) {
                        <label class="check">
                          <input type="checkbox" [checked]="l.symbols.includes(symbol())" (change)="toggleList(l)" />
                          {{ l.name }}
                        </label>
                      }
                    </div>
                  </details>
                }
              </h1>
              <p class="muted">{{ q.name }} · {{ q.exchange }} · {{ q.currency }}</p>
            </div>
            <div class="quote-price">
              <p class="hero-value mono" [appFlash]="q.price">{{ q.price | money: q.currency }}</p>
              <p class="mono" [class]="q.change | pnlClass">
                {{ q.change | signedMoney: q.currency }} ({{ q.changePercent | signedPercent }})
              </p>
              @if (q.market && q.market.detail !== info.status()?.detail) {
                <!-- Only for an exchange on different hours. The bar at the top of the page already covers the US market. -->
                <p class="small muted">
                  <span class="tag" [class.tag-open]="q.isMarketOpen">{{ q.market.label }}</span>
                  {{ q.market.detail }}
                </p>
              }
              @if (!q.isMarketOpen && q.extendedPrice !== null) {
                <p class="small mono">
                  <span class="muted">{{ q.extendedSession }}</span> {{ q.extendedPrice | money: q.currency }}
                  <span [class]="q.extendedChangePercent | pnlClass">{{ q.extendedChangePercent | signedPercent }}</span>
                </p>
              }
              @if (lastTrade(); as when) { <p class="small muted">Last trade {{ when }}</p> }
            </div>
          </header>

          <div class="trade-grid">
            <div class="trade-main">
              <section class="panel">
                <div class="segmented" role="group" aria-label="Chart range">
                  @for (r of ranges; track r) {
                    <button type="button" [class.on]="range() === r" [attr.aria-pressed]="range() === r" (click)="range.set(r)">{{ r }}</button>
                  }
                </div>
                @if (candlesError()) {
                  <p class="notice notice-bad">The chart couldn't load. Trading still works at the price above.</p>
                }
                <app-price-chart [candles]="candles()" [range]="range()" [quote]="quote()" [lines]="chartLines()" [ariaLabel]="q.symbol + ' price chart, ' + range()" />
              </section>

              <section class="panel">
                <dl class="stats stats-wide">
                  <div><dt>Previous close</dt><dd class="mono">{{ q.previousClose | money: q.currency }}</dd></div>
                  <div><dt>Day low</dt><dd class="mono">{{ q.dayLow | money: q.currency }}</dd></div>
                  <div><dt>Day high</dt><dd class="mono">{{ q.dayHigh | money: q.currency }}</dd></div>
                  <div><dt>Volume</dt><dd class="mono">{{ q.volume | compact }}</dd></div>
                  <div><dt>52-week low</dt><dd class="mono">{{ q.fiftyTwoWeekLow | money: q.currency }}</dd></div>
                  <div><dt>52-week high</dt><dd class="mono">{{ q.fiftyTwoWeekHigh | money: q.currency }}</dd></div>
                </dl>
              </section>

              @if (creatorClaims().length) {
                <section class="panel">
                  <h2>What creators have said about {{ q.symbol }}</h2>
                  <div class="claim-list">
                    @for (c of creatorClaims(); track c.id) { <app-claim-card [claim]="c" [showTicker]="false" /> }
                  </div>
                </section>
              }

              @if (symbolOrders().length) {
                <section class="panel">
                  <h2>Open orders for {{ q.symbol }}</h2>
                  <div class="table-wrap">
                    <table>
                      <thead><tr><th>Order</th><th class="num">Qty</th><th class="num">Trigger</th><th></th></tr></thead>
                      <tbody>
                        @for (o of symbolOrders(); track o.id) {
                          <tr>
                            <td>{{ o.side }} {{ o.type.toLowerCase() }}</td>
                            <td class="num">{{ o.quantity | qty }}</td>
                            <td class="num">{{ o.limitPrice ?? o.stopPrice | money }}</td>
                            <td class="num"><button type="button" class="ghost" (click)="cancel(o)">Cancel</button></td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>
                </section>
              }
            </div>

            <aside class="ticket" aria-label="Order ticket">
              <div class="segmented segmented-full side-toggle" role="group" aria-label="Side">
                <button type="button" class="side-buy" [class.on]="side() === 'Buy'" [attr.aria-pressed]="side() === 'Buy'" (click)="side.set('Buy')">Buy</button>
                <button type="button" class="side-sell" [class.on]="side() === 'Sell'" [attr.aria-pressed]="side() === 'Sell'" (click)="side.set('Sell')">Sell</button>
              </div>

              <div class="segmented segmented-full" role="group" aria-label="Order type">
                @for (t of types; track t) {
                  <button type="button" [class.on]="type() === t" [attr.aria-pressed]="type() === t" (click)="setType(t)">{{ t }}</button>
                }
              </div>

              @if (followedClaim(); as c) {
                <p class="form-hint">
                  Following {{ c.channelTitle }}'s {{ c.direction.toLowerCase() }} call.
                  <button type="button" class="link" (click)="followedClaimId.set(null)">Remove</button>
                </p>
              }

              <form (submit)="submit($event)">
                <label>
                  Quantity
                  <span class="input-row">
                    <input #qtyBox class="mono" type="number" inputmode="decimal" min="0" step="any" placeholder="0" [value]="qtyText()" (input)="qtyText.set(qtyBox.value)" />
                    <button type="button" class="ghost" (click)="useMax()" [disabled]="maxQuantity() <= 0">Max</button>
                  </span>
                </label>

                @if (type() !== 'Market') {
                  <label>
                    {{ type() }} price
                    <input #priceBox class="mono" type="number" inputmode="decimal" min="0" step="any" [value]="priceText()" (input)="priceText.set(priceBox.value)" />
                    <small class="muted">{{ typeHelp() }}</small>
                  </label>
                }

                <dl class="ticket-sums">
                  <div>
                    <dt>{{ side() === 'Buy' ? 'Estimated cost' : 'Estimated proceeds' }}</dt>
                    <dd class="mono">{{ estimate() | money }}</dd>
                  </div>
                  @if (side() === 'Buy') {
                    <div><dt>Buying power</dt><dd class="mono">{{ portfolio()?.buyingPower | money }}</dd></div>
                  } @else {
                    <div><dt>Shares available</dt><dd class="mono">{{ availableToSell() | qty }}</dd></div>
                  }
                  @if (info.info().commissionPerOrder > 0) {
                    <div><dt>Commission</dt><dd class="mono">{{ info.info().commissionPerOrder | money }}</dd></div>
                  }
                </dl>

                @if (blockReason(); as reason) { <p class="form-hint">{{ reason }}</p> }
                @if (orderError(); as message) { <p class="form-error" role="alert">{{ message }}</p> }

                <button type="submit" class="submit" [class.submit-buy]="side() === 'Buy'" [class.submit-sell]="side() === 'Sell'" [disabled]="!canSubmit()">
                  {{ submitLabel() }}
                </button>

                @if (!q.isMarketOpen && type() !== 'Market' && !blockReason()) {
                  <p class="small muted">The market is closed, so this waits. It can only fill after the open, at the first price that meets your {{ type().toLowerCase() }}.</p>
                }
              </form>

              @if (position(); as p) {
                <div class="ticket-position">
                  <h3>Your position</h3>
                  <dl class="ticket-sums">
                    <div><dt>Shares</dt><dd class="mono">{{ p.quantity | qty }}</dd></div>
                    <div><dt>Average cost</dt><dd class="mono">{{ p.averageCost | money }}</dd></div>
                    <div><dt>Value</dt><dd class="mono">{{ p.quantity * q.price | money }}</dd></div>
                    <div>
                      <dt>Unrealized</dt>
                      <dd class="mono" [class]="p.quantity * (q.price - p.averageCost) | pnlClass">{{ p.quantity * (q.price - p.averageCost) | signedMoney }}</dd>
                    </div>
                  </dl>
                </div>
              }
            </aside>
          </div>
        }
      }
    }
  `,
})
export class TradePage {
  /** Bound from the route. */
  readonly symbolParam = input.required<string>({ alias: 'symbol' });

  /** Bound from ?claim=, when arriving from a creator's call. */
  readonly claim = input<string | undefined>();

  private api = inject(ApiService);
  private live = inject(LiveService);
  private toasts = inject(ToastService);
  protected info = inject(MarketInfoStore);

  protected readonly ranges: ChartRange[] = ['1D', '5D', '1M', '6M', '1Y', '5Y'];
  protected readonly types: OrderType[] = ['Market', 'Limit', 'Stop'];

  protected readonly symbol = computed(() => this.symbolParam().toUpperCase());
  protected readonly range = signal<ChartRange>('6M');
  protected readonly state = signal<LoadState>('loading');
  protected readonly loadError = signal('');
  protected readonly candles = signal<Candle[]>([]);
  protected readonly candlesError = signal(false);
  protected readonly portfolio = signal<Portfolio | null>(null);
  protected readonly openOrders = signal<Order[]>([]);
  protected readonly lists = signal<WatchList[]>([]);
  protected readonly creatorClaims = signal<Claim[]>([]);
  protected readonly followedClaimId = signal<string | null>(null);
  private readonly initialQuote = signal<Quote | null>(null);

  protected readonly side = signal<OrderSide>('Buy');
  protected readonly type = signal<OrderType>('Market');
  protected readonly qtyText = signal('');
  protected readonly priceText = signal('');
  protected readonly orderError = signal<string | null>(null);
  protected readonly submitting = signal(false);

  protected readonly quote = computed(() => this.live.quotes()[this.symbol()] ?? this.initialQuote());
  protected readonly mainList = computed(() => this.lists().find((l) => l.isDefault) ?? null);
  protected readonly favorite = computed(() => this.mainList()?.symbols.includes(this.symbol()) ?? false);
  protected readonly followedClaim = computed(() => this.creatorClaims().find((c) => c.id === this.followedClaimId()) ?? null);
  protected readonly position = computed(() => this.portfolio()?.positions.find((p) => p.symbol === this.symbol()) ?? null);
  protected readonly symbolOrders = computed(() => this.openOrders().filter((o) => o.symbol === this.symbol()));

  protected readonly lastTrade = computed(() => {
    const time = this.quote()?.priceTime;
    return time ? formatMarketTime(new Date(time), { weekday: 'short', month: 'short', day: 'numeric' }) : null;
  });

  private readonly quantity = computed(() => Number(this.qtyText()) || 0);
  private readonly triggerPrice = computed(() => Number(this.priceText()) || 0);
  private readonly referencePrice = computed(() => (this.type() === 'Market' ? (this.quote()?.price ?? 0) : this.triggerPrice()));

  protected readonly estimate = computed(() => {
    const value = this.quantity() * this.referencePrice();
    const fee = this.info.info().commissionPerOrder;
    return this.side() === 'Buy' ? value + fee : Math.max(value - fee, 0);
  });

  protected readonly availableToSell = computed(() => {
    const held = this.position()?.quantity ?? 0;
    const inOrders = this.symbolOrders().filter((o) => o.side === 'Sell').reduce((sum, o) => sum + o.quantity, 0);
    return Math.max(held - inOrders, 0);
  });

  protected readonly maxQuantity = computed(() => {
    if (this.side() === 'Sell') return this.availableToSell();
    const price = this.referencePrice();
    const power = (this.portfolio()?.buyingPower ?? 0) - this.info.info().commissionPerOrder;
    return price > 0 && power > 0 ? Math.floor(power / price) : 0;
  });

  protected readonly typeHelp = computed(() => {
    const buying = this.side() === 'Buy';
    return this.type() === 'Limit'
      ? buying ? 'Fills once the price is at or below this.' : 'Fills once the price is at or above this.'
      : buying ? 'Buys once the price rises to this.' : 'Sells once the price falls to this.';
  });

  /** Why the order can't be sent yet, when the ticket can say so before the server does. */
  protected readonly blockReason = computed(() => {
    const q = this.quote();
    if (!q) return null;
    const base = this.info.info().baseCurrency;
    if (!q.isTradable) return `${q.symbol} is an index or other non-tradable instrument. Only stocks and ETFs can be traded.`;
    if (q.currency.toUpperCase() !== base.toUpperCase()) return `${q.symbol} trades in ${q.currency}. This account trades in ${base}, and currency conversion isn't supported.`;
    if (this.side() === 'Sell' && this.availableToSell() === 0) return `You have no ${q.symbol} shares available to sell. Short selling isn't supported.`;
    if (!q.isMarketOpen && this.type() === 'Market') {
      const when = q.market ? `${q.market.label}. ${q.market.detail}.` : 'The market is closed.';
      return `${when} A market order can't wait, but a limit or stop order can.`;
    }
    return null;
  });

  protected readonly canSubmit = computed(
    () => !this.submitting() && !this.blockReason() && this.quantity() > 0 && this.referencePrice() > 0,
  );

  protected readonly submitLabel = computed(() => {
    if (this.submitting()) return 'Placing order…';
    const quantity = this.quantity();
    const verb = this.side();
    const what = quantity > 0 ? `${verb} ${quantity} ${this.symbol()}` : `${verb} ${this.symbol()}`;
    return this.type() === 'Market' ? what : `${what} · ${this.type().toLowerCase()}`;
  });

  protected readonly chartLines = computed<ChartLine[]>(() => {
    const lines: ChartLine[] = this.symbolOrders().map((o) => ({
      price: (o.limitPrice ?? o.stopPrice)!,
      label: `${o.side} ${o.type.toLowerCase()}`,
      tone: o.side === 'Buy' ? 'gain' : 'loss',
    }));
    const position = this.position();
    if (position) lines.push({ price: position.averageCost, label: 'Avg cost', tone: 'neutral' });
    return lines;
  });

  constructor() {
    const symbol$ = toObservable(this.symbol);

    symbol$
      .pipe(
        switchMap((symbol) => {
          this.state.set('loading');
          this.initialQuote.set(null);
          this.resetTicket();
          return this.api.quote(symbol).pipe(
            map((quote) => ({ quote, error: null as unknown })),
            catchError((error: unknown) => of({ quote: null, error })),
          );
        }),
        takeUntilDestroyed(),
      )
      .subscribe(({ quote, error }) => {
        if (quote) {
          this.initialQuote.set(quote);
          this.live.setQuote(quote);
          this.state.set('ready');
        } else if ((error as { status?: number })?.status === 404 || (error as { status?: number })?.status === 400) {
          this.state.set('not-found');
        } else {
          this.loadError.set(errorMessage(error));
          this.state.set('failed');
        }
      });

    combineLatest([symbol$, toObservable(this.range)])
      .pipe(
        switchMap(([symbol, range]) =>
          this.api.candles(symbol, range).pipe(
            map((candles) => ({ candles, failed: false })),
            catchError(() => of({ candles: [] as Candle[], failed: true })),
            startWith(null),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((result) => {
        if (!result) {
          // Drop the old bars right away, so live ticks for the new symbol can't be drawn onto them.
          this.candles.set([]);
          return;
        }
        this.candles.set(result.candles);
        this.candlesError.set(result.failed);
      });

    effect((onCleanup) => onCleanup(this.live.watch([this.symbol()])));

    this.refreshAccount();
    this.live.orderUpdates.pipe(takeUntilDestroyed()).subscribe(() => this.refreshAccount());
    this.api.watchLists().subscribe((lists) => this.lists.set(lists));

    symbol$
      .pipe(
        switchMap((symbol) => this.api.claims(symbol).pipe(catchError(() => of<Claim[]>([])))),
        takeUntilDestroyed(),
      )
      .subscribe((claims) => {
        this.creatorClaims.set(claims);
        const chosen = claims.find((c) => c.id === this.claim());
        if (chosen) {
          this.followedClaimId.set(chosen.id);
          this.side.set(chosen.direction === 'Bullish' ? 'Buy' : 'Sell');
        }
      });
  }

  protected setType(type: OrderType): void {
    this.type.set(type);
    if (type !== 'Market' && !this.priceText()) {
      this.priceText.set(this.quote()?.price.toFixed(2) ?? '');
    }
    this.orderError.set(null);
  }

  protected useMax(): void {
    this.qtyText.set(String(this.maxQuantity()));
  }

  protected submit(event: Event): void {
    event.preventDefault();
    if (!this.canSubmit()) return;

    const type = this.type();
    this.submitting.set(true);
    this.orderError.set(null);

    this.api
      .placeOrder({
        symbol: this.symbol(),
        side: this.side(),
        type,
        quantity: this.quantity(),
        limitPrice: type === 'Limit' ? this.triggerPrice() : null,
        stopPrice: type === 'Stop' ? this.triggerPrice() : null,
        claimId: this.followedClaimId(),
      })
      .subscribe({
        next: (order) => {
          this.submitting.set(false);
          this.qtyText.set('');
          this.announce(order);
          this.refreshAccount();
        },
        error: (err: unknown) => {
          this.submitting.set(false);
          this.orderError.set(errorMessage(err));
        },
      });
  }

  protected cancel(order: Order): void {
    this.api.cancelOrder(order.id).subscribe({
      next: () => {
        this.toasts.show('info', `Cancelled ${order.side.toLowerCase()} ${order.symbol}.`);
        this.refreshAccount();
      },
      error: (err: unknown) => {
        this.toasts.show('bad', errorMessage(err));
        this.refreshAccount();
      },
    });
  }

  protected toggleList(list: WatchList | null): void {
    if (!list) return;
    const symbol = this.symbol();
    const request = list.symbols.includes(symbol)
      ? this.api.removeFromWatchList(list.id, symbol)
      : this.api.addToWatchList(list.id, symbol);
    request.subscribe({
      next: () => this.api.watchLists().subscribe((lists) => this.lists.set(lists)),
      error: (err: unknown) => this.toasts.show('bad', errorMessage(err)),
    });
  }

  private announce(order: Order): void {
    if (order.status === 'Filled') {
      const verb = order.side === 'Buy' ? 'Bought' : 'Sold';
      this.toasts.show('good', `${verb} ${order.quantity} ${order.symbol} at ${order.filledPrice?.toFixed(2)}`);
    } else if (order.status === 'Open') {
      const placed = `${order.side} ${order.type.toLowerCase()} for ${order.quantity} ${order.symbol}`;
      this.toasts.show('info', this.quote()?.isMarketOpen ? `${placed} is now open.` : `${placed} is placed, and waits for the market to open.`);
    } else if (order.status === 'Rejected') {
      this.toasts.show('bad', order.rejectReason ?? 'The order was rejected.');
    }
  }

  private refreshAccount(): void {
    this.api.portfolio().subscribe((p) => this.portfolio.set(p));
    this.api.orders(true).subscribe((o) => this.openOrders.set(o));
  }

  private resetTicket(): void {
    this.side.set('Buy');
    this.type.set('Market');
    this.qtyText.set('');
    this.priceText.set('');
    this.orderError.set(null);
    this.followedClaimId.set(null);
  }
}
