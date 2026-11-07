import { Component, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { ApiService, errorMessage } from '../core/api.service';
import { LiveService } from '../core/live.service';
import { MarketInfoStore } from '../core/market-info.store';
import { ClaimTrade, EquityPoint, Order, Portfolio } from '../core/models';
import { ToastService } from '../core/toast.service';
import { EquityChartComponent } from '../shared/equity-chart.component';
import { FlashDirective } from '../shared/flash.directive';
import { FORMAT_PIPES } from '../shared/format';

@Component({
  selector: 'app-portfolio-page',
  imports: [RouterLink, EquityChartComponent, FlashDirective, ...FORMAT_PIPES],
  template: `
    @if (error(); as message) {
      <div class="notice notice-bad" role="alert">
        {{ message }} <button type="button" class="link" (click)="load()">Try again</button>
      </div>
    }

    @if (view(); as v) {
      <section class="hero">
        <div>
          <p class="eyebrow">Account value</p>
          <p class="hero-value mono" [appFlash]="v.equity">{{ v.equity | money }}</p>
          <p class="hero-delta mono">
            <span [class]="v.dayChange | pnlClass">{{ v.dayChange | signedMoney }}</span>
            <span class="muted"> today</span>
            <span class="sep">·</span>
            <span [class]="v.totalReturn | pnlClass">{{ v.totalReturn | signedMoney }} ({{ v.totalReturnPercent | signedPercent }})</span>
            <span class="muted"> since start</span>
          </p>
        </div>
        <dl class="stats">
          <div><dt>Cash</dt><dd class="mono">{{ v.cash | money }}</dd></div>
          <div>
            <dt>Buying power</dt>
            <dd class="mono">{{ v.buyingPower | money }}</dd>
          </div>
          <div><dt>Unrealized P&amp;L</dt><dd class="mono" [class]="v.unrealized | pnlClass">{{ v.unrealized | signedMoney }}</dd></div>
          <div><dt>Realized P&amp;L</dt><dd class="mono" [class]="v.realizedPnl | pnlClass">{{ v.realizedPnl | signedMoney }}</dd></div>
          <div>
            <dt>Win rate</dt>
            <dd class="mono">
              @if (v.winRate === null) { — } @else { {{ v.winRate }}% <span class="muted small">of {{ v.closedTrades }}</span> }
            </dd>
          </div>
        </dl>
      </section>

      <section class="panel">
        <h2>Your shares</h2>
        @if (v.rows.length === 0) {
          <div class="empty">
            <p>You don't own any shares yet. You have {{ v.cash | money }} ready to trade.</p>
            @if (info.status(); as s) {
              @if (!s.isOpen) { <p class="muted">{{ s.label }}. {{ s.detail }}. Until then, a limit or stop order can wait for the open.</p> }
            }
            <p class="muted">Search for any ticker above, or start with one of these:</p>
            <div class="chips">
              @for (s of starters; track s) { <a class="chip mono" [routerLink]="['/trade', s]">{{ s }}</a> }
            </div>
          </div>
        } @else {
          <div class="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Symbol</th>
                  <th class="num">Shares</th>
                  <th class="num">Avg cost</th>
                  <th class="num">Last</th>
                  <th class="num">Value</th>
                  <th class="num">Weight</th>
                  <th class="num">Today</th>
                  <th class="num">Unrealized</th>
                </tr>
              </thead>
              <tbody>
                @for (p of v.rows; track p.symbol) {
                  <tr class="row-link" tabindex="0" (click)="open(p.symbol)" (keydown.enter)="open(p.symbol)">
                    <td>
                      <a class="mono strong" [routerLink]="['/trade', p.symbol]">{{ p.symbol }}</a>
                      <span class="cell-sub">{{ p.name }}</span>
                    </td>
                    <td class="num">{{ p.quantity | qty }}</td>
                    <td class="num">{{ p.averageCost | money }}</td>
                    <td class="num" [appFlash]="p.lastPrice">
                      {{ p.lastPrice | money }}
                      @if (p.priceUnavailable) { <span class="cell-sub">no price</span> }
                    </td>
                    <td class="num">{{ p.marketValue | money }}</td>
                    <td class="num">{{ p.weight.toFixed(1) }}%</td>
                    <td class="num" [class]="p.dayChange | pnlClass">{{ p.dayChange | signedMoney }}</td>
                    <td class="num" [class]="p.unrealizedPnl | pnlClass">
                      {{ p.unrealizedPnl | signedMoney }}
                      <span class="cell-sub">{{ p.unrealizedPnlPercent | signedPercent }}</span>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>

      <section class="panel">
        <h2>Account value over time</h2>
        <app-equity-chart [points]="history()" [startingBalance]="v.startingBalance" />
      </section>

      @if (claimTrades().length) {
        <section class="panel">
          <h2>Trades from creators' calls</h2>
          <div class="table-wrap">
            <table>
              <thead>
                <tr><th>Symbol</th><th>Creator</th><th>Their call</th><th>Your trade</th><th class="num">Fill</th><th class="num">Now</th><th class="num">Profit or loss</th></tr>
              </thead>
              <tbody>
                @for (t of claimTrades(); track t.orderId) {
                  <tr>
                    <td><a class="mono strong" [routerLink]="['/trade', t.symbol]">{{ t.symbol }}</a></td>
                    <td>{{ t.channelTitle }}</td>
                    <td>{{ t.direction === 'Bullish' ? '▲ Bullish' : '▼ Bearish' }}</td>
                    <td>{{ t.side }} {{ t.quantity | qty }} @if (t.status !== 'Filled') { <span class="tag">{{ t.status }}</span> }</td>
                    <td class="num">{{ t.fillPrice | money }}</td>
                    <td class="num">{{ t.currentPrice | money }}</td>
                    <td class="num" [class]="t.profitLoss | pnlClass">{{ t.profitLoss | signedMoney }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>
      }

      @if (openOrders().length) {
        <section class="panel">
          <h2>Open orders <a class="panel-link" routerLink="/orders">View all</a></h2>
          <div class="table-wrap">
            <table>
              <thead>
                <tr><th>Symbol</th><th>Order</th><th class="num">Qty</th><th class="num">Trigger</th><th></th></tr>
              </thead>
              <tbody>
                @for (o of openOrders(); track o.id) {
                  <tr>
                    <td><a class="mono strong" [routerLink]="['/trade', o.symbol]">{{ o.symbol }}</a></td>
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
    } @else if (!error()) {
      <p class="muted loading">Loading your portfolio…</p>
    }
  `,
})
export class PortfolioPage {
  private api = inject(ApiService);
  private live = inject(LiveService);
  private router = inject(Router);
  private toasts = inject(ToastService);
  protected info = inject(MarketInfoStore);

  protected readonly starters = ['AAPL', 'MSFT', 'NVDA', 'SPY', 'TSLA'];
  protected readonly portfolio = signal<Portfolio | null>(null);
  protected readonly history = signal<EquityPoint[]>([]);
  protected readonly openOrders = signal<Order[]>([]);
  protected readonly claimTrades = signal<ClaimTrade[]>([]);
  protected readonly error = signal<string | null>(null);

  private readonly symbols = computed(() => this.portfolio()?.positions.map((p) => p.symbol) ?? []);

  /** Portfolio totals re-priced with the latest live quotes, so the page moves between server loads. */
  protected readonly view = computed(() => {
    const base = this.portfolio();
    if (!base) return null;

    const quotes = this.live.quotes();
    const positions = base.positions.map((p) => {
      const quote = quotes[p.symbol];
      const lastPrice = quote?.price ?? p.lastPrice;
      const marketValue = p.quantity * lastPrice;
      const unrealizedPnl = marketValue - p.costBasis;
      return {
        ...p,
        lastPrice,
        marketValue,
        unrealizedPnl,
        unrealizedPnlPercent: p.costBasis === 0 ? 0 : (unrealizedPnl / p.costBasis) * 100,
        dayChange: p.quantity * (lastPrice - p.dayBasePrice),
        priceUnavailable: p.priceUnavailable && !quote,
      };
    });

    const positionsValue = positions.reduce((sum, p) => sum + p.marketValue, 0);
    const equity = base.cash + positionsValue;
    const totalReturn = equity - base.startingBalance;
    return {
      ...base,
      equity,
      totalReturn,
      totalReturnPercent: base.startingBalance === 0 ? 0 : (totalReturn / base.startingBalance) * 100,
      dayChange: positions.reduce((sum, p) => sum + p.dayChange, 0),
      unrealized: positions.reduce((sum, p) => sum + p.unrealizedPnl, 0),
      rows: positions.map((p) => ({ ...p, weight: equity === 0 ? 0 : (p.marketValue / equity) * 100 })),
    };
  });

  constructor() {
    this.load();
    this.live.orderUpdates.pipe(takeUntilDestroyed()).subscribe(() => this.load());

    effect((onCleanup) => {
      const symbols = this.symbols();
      if (symbols.length) onCleanup(this.live.watch(symbols));
    });
  }

  protected load(): void {
    forkJoin({
      portfolio: this.api.portfolio(),
      history: this.api.equityHistory(),
      orders: this.api.orders(true),
      claimTrades: this.api.claimTrades(),
    }).subscribe({
      next: ({ portfolio, history, orders, claimTrades }) => {
        this.portfolio.set(portfolio);
        this.history.set(history);
        this.openOrders.set(orders);
        this.claimTrades.set(claimTrades);
        this.error.set(null);
      },
      error: (err: unknown) => this.error.set(errorMessage(err)),
    });
  }

  protected open(symbol: string): void {
    void this.router.navigate(['/trade', symbol]);
  }

  protected cancel(order: Order): void {
    this.api.cancelOrder(order.id).subscribe({
      next: () => {
        this.toasts.show('info', `Cancelled ${order.side.toLowerCase()} ${order.symbol}.`);
        this.load();
      },
      error: (err: unknown) => {
        this.toasts.show('bad', errorMessage(err));
        this.load();
      },
    });
  }
}
