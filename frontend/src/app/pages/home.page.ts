import { Component, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { catchError, of, switchMap, timer } from 'rxjs';
import { ApiService, errorMessage } from '../core/api.service';
import { LiveService } from '../core/live.service';
import { MarketInfoStore } from '../core/market-info.store';
import { MarketMovers, Portfolio, Quote } from '../core/models';
import { FlashDirective } from '../shared/flash.directive';
import { FORMAT_PIPES, formatMarketTime } from '../shared/format';

interface MoverList {
  title: string;
  note: string;
  rows: Quote[];
  showVolume: boolean;
}

/** How often to ask for new rankings. Prices between those refreshes arrive live. */
const LIST_REFRESH_MS = 60_000;

@Component({
  selector: 'app-home-page',
  imports: [RouterLink, FlashDirective, ...FORMAT_PIPES],
  template: `
    @if (error(); as message) {
      <div class="notice notice-bad" role="alert">{{ message }}</div>
    }

    @if (indexes().length) {
      <section class="index-strip" aria-label="Market indexes">
        @for (q of indexes(); track q.symbol) {
          <a [routerLink]="['/trade', q.symbol]">
            <span class="index-name">{{ q.name }}</span>
            <span class="index-value mono" [appFlash]="q.price">{{ number(q.price) }}</span>
            <span class="small mono" [class]="q.change | pnlClass">{{ signed(q.change) }} ({{ q.changePercent | signedPercent }})</span>
          </a>
        }
      </section>
    }

    @if (holdings().length) {
      <section class="panel">
        <h2>Your shares <a class="panel-link" routerLink="/portfolio">View portfolio</a></h2>
        <div class="table-wrap">
          <table>
            <thead><tr><th>Symbol</th><th class="num">Shares</th><th class="num">Price</th><th class="num">Value</th><th class="num">Today</th></tr></thead>
            <tbody>
              @for (h of holdings(); track h.symbol) {
                <tr class="row-link" tabindex="0" (click)="open(h.symbol)" (keydown.enter)="open(h.symbol)">
                  <td><a class="mono strong" [routerLink]="['/trade', h.symbol]">{{ h.symbol }}</a><span class="cell-sub">{{ h.name }}</span></td>
                  <td class="num">{{ h.quantity | qty }}</td>
                  <td class="num" [appFlash]="h.price">{{ h.price | money }}</td>
                  <td class="num">{{ h.value | money }}</td>
                  <td class="num" [class]="h.dayChange | pnlClass">{{ h.dayChange | signedMoney }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </section>
    }

    @if (lists().length) {
      <p class="movers-note">{{ asOfNote() }}</p>
      @if (movers()?.stale) {
        <p class="notice">Yahoo Finance didn't return a new list just now, so some rankings may be a little old.</p>
      }
      <div class="movers-grid">
        @for (list of lists(); track list.title) {
          <section class="panel">
            <h2>{{ list.title }}</h2>
            <p class="movers-note">{{ list.note }}</p>
            @if (list.rows.length === 0) {
              <p class="muted small">Nothing to show right now.</p>
            } @else {
              <div class="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>Symbol</th>
                      <th class="num">Price</th>
                      <th class="num">Today</th>
                      @if (list.showVolume) { <th class="num">Volume</th> }
                    </tr>
                  </thead>
                  <tbody>
                    @for (q of list.rows; track q.symbol) {
                      <tr class="row-link" tabindex="0" (click)="open(q.symbol)" (keydown.enter)="open(q.symbol)">
                        <td><a class="mono strong" [routerLink]="['/trade', q.symbol]">{{ q.symbol }}</a><span class="cell-sub">{{ q.name }}</span></td>
                        <td class="num" [appFlash]="q.price">{{ q.price | money: q.currency }}</td>
                        <td class="num" [class]="q.change | pnlClass">
                          {{ q.changePercent | signedPercent }}
                          <span class="cell-sub">{{ q.change | signedMoney: q.currency }}</span>
                        </td>
                        @if (list.showVolume) { <td class="num">{{ q.volume | compact }}</td> }
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </section>
        }
      </div>
    } @else if (!error()) {
      <p class="muted loading">Loading the market…</p>
    }
  `,
})
export class HomePage {
  private api = inject(ApiService);
  private live = inject(LiveService);
  private router = inject(Router);
  private info = inject(MarketInfoStore);

  protected readonly movers = signal<MarketMovers | null>(null);
  protected readonly error = signal<string | null>(null);
  private readonly portfolio = signal<Portfolio | null>(null);

  /** The newest price for a row. Rankings only change on a list refresh, so rows don't jump around under the mouse. */
  private withLive(rows: Quote[] | undefined): Quote[] {
    const quotes = this.live.quotes();
    return (rows ?? []).map((q) => quotes[q.symbol] ?? q);
  }

  protected readonly indexes = computed(() => this.withLive(this.movers()?.indexes));

  protected readonly lists = computed<MoverList[]>(() => {
    const m = this.movers();
    if (!m) return [];
    return [
      { title: 'Trending', note: 'What people are looking up most on Yahoo Finance', rows: this.withLive(m.trending), showVolume: false },
      { title: 'Top gainers', note: 'Biggest rises today among larger US companies', rows: this.withLive(m.gainers), showVolume: false },
      { title: 'Top losers', note: 'Biggest falls today among larger US companies', rows: this.withLive(m.losers), showVolume: false },
      { title: 'Most active', note: 'Most shares traded today', rows: this.withLive(m.mostActive), showVolume: true },
    ];
  });

  protected readonly holdings = computed(() => {
    const quotes = this.live.quotes();
    return (this.portfolio()?.positions ?? []).map((p) => {
      const price = quotes[p.symbol]?.price ?? p.lastPrice;
      return {
        symbol: p.symbol,
        name: p.name,
        quantity: p.quantity,
        price,
        value: p.quantity * price,
        dayChange: p.quantity * (price - p.dayBasePrice),
      };
    });
  });

  /** One sorted key, so a list refresh with the same symbols doesn't drop and re-add every subscription. */
  private readonly watchKey = computed(() => {
    const m = this.movers();
    const symbols = new Set([
      ...(this.portfolio()?.positions.map((p) => p.symbol) ?? []),
      ...(m ? [...m.indexes, ...m.trending, ...m.gainers, ...m.losers, ...m.mostActive].map((q) => q.symbol) : []),
    ]);
    return [...symbols].sort().join(',');
  });

  protected readonly asOfNote = computed(() => {
    const status = this.info.status();
    if (status?.isOpen) return 'Prices update live. Rankings refresh every minute.';

    const times = this.movers()?.gainers.map((q) => q.priceTime).filter((t): t is string => !!t) ?? [];
    if (times.length === 0) return 'Rankings refresh every minute.';
    const latest = times.reduce((a, b) => (a > b ? a : b));
    const when = formatMarketTime(new Date(latest), { weekday: 'short', month: 'short', day: 'numeric' });
    return `The market is closed, so these are from its last session, as of ${when}.`;
  });

  constructor() {
    timer(0, LIST_REFRESH_MS)
      .pipe(
        switchMap(() =>
          this.api.movers().pipe(
            catchError((err: unknown) => {
              // Keep showing the last good lists if there are any.
              if (!this.movers()) this.error.set(errorMessage(err));
              return of(null);
            }),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((movers) => {
        if (!movers) return;
        this.movers.set(movers);
        this.error.set(null);
      });

    this.api.portfolio().subscribe((p) => this.portfolio.set(p));
    this.live.orderUpdates.pipe(takeUntilDestroyed()).subscribe(() => this.api.portfolio().subscribe((p) => this.portfolio.set(p)));

    effect((onCleanup) => {
      const key = this.watchKey();
      if (key) onCleanup(this.live.watch(key.split(',')));
    });
  }

  protected open(symbol: string): void {
    void this.router.navigate(['/trade', symbol]);
  }

  /** Index levels are points, not dollars. */
  protected number(value: number): string {
    return value.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  }

  protected signed(value: number): string {
    return `${value > 0 ? '+' : value < 0 ? '−' : ''}${this.number(Math.abs(value))}`;
  }
}
