import { Component, OnDestroy, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, timer } from 'rxjs';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ApiService } from './core/api.service';
import { AuthService } from './core/auth.service';
import { LiveService } from './core/live.service';
import { MarketInfoStore } from './core/market-info.store';
import { Order } from './core/models';
import { ThemeService } from './core/theme.service';
import { ToastService } from './core/toast.service';
import { MARKET_ZONE } from './shared/format';
import { SymbolSearchComponent } from './shared/symbol-search.component';

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, SymbolSearchComponent],
  template: `
    <header class="topbar">
      <a class="wordmark" routerLink="/home">Paperdesk</a>
      <nav aria-label="Main">
        <a routerLink="/home" routerLinkActive="current">Home</a>
        <a routerLink="/portfolio" routerLinkActive="current">Portfolio</a>
        <a routerLink="/watchlist" routerLinkActive="current">Lists</a>
        <a routerLink="/orders" routerLinkActive="current">Orders</a>
        <a routerLink="/creators" routerLinkActive="current">Creators</a>
        <a routerLink="/digest" routerLinkActive="current">Digest</a>
        <a routerLink="/account" routerLinkActive="current">Account</a>
      </nav>
      <app-symbol-search class="topbar-search" (picked)="openSymbol($event)" />
      <div class="topbar-end">
        <span class="feed" [title]="feedDetail()" [class.feed-sim]="info.info().simulated">
          <span class="dot" [class.dot-ok]="live.state() === 'connected'" aria-hidden="true"></span>
          {{ feedLabel() }}
        </span>
        <button type="button" class="ghost" (click)="theme.toggle()" [attr.aria-label]="'Switch to ' + (theme.theme() === 'dark' ? 'light' : 'dark') + ' theme'">
          {{ theme.theme() === 'dark' ? 'Light' : 'Dark' }}
        </button>
        <button type="button" class="ghost" (click)="signOut()">Sign out</button>
      </div>
    </header>

    @if (info.status(); as s) {
      <div class="market-strip" role="status">
        <span class="tag" [class.tag-open]="s.isOpen">{{ s.label }}</span>
        <span>{{ s.detail }}</span>
        @if (!info.info().simulated) { <span class="muted">{{ newYorkNow() }} in New York</span> }
      </div>
    }

    <main class="page">
      <router-outlet />
    </main>

    <footer class="page-foot">
      Practice money only. No real orders are placed. {{ priceNote() }} Not investment advice.
    </footer>

    <div class="toasts" role="status" aria-live="polite">
      @for (t of toasts.toasts(); track t.id) {
        <div class="toast" [class]="'toast toast-' + t.kind">
          <span>{{ t.text }}</span>
          <button type="button" class="ghost" aria-label="Dismiss" (click)="toasts.dismiss(t.id)">×</button>
        </div>
      }
    </div>
  `,
})
export class ShellComponent implements OnDestroy {
  protected auth = inject(AuthService);
  protected live = inject(LiveService);
  protected info = inject(MarketInfoStore);
  protected theme = inject(ThemeService);
  protected toasts = inject(ToastService);
  private router = inject(Router);
  private api = inject(ApiService);

  protected readonly newYorkNow = signal('');

  protected readonly feedLabel = computed(() => {
    const state = this.live.state();
    if (state === 'reconnecting' || state === 'connecting') return 'Reconnecting…';
    if (state === 'disconnected') return 'Offline';
    return this.info.info().streaming ? `${this.info.info().provider} · Live` : this.info.info().provider;
  });

  protected readonly priceNote = computed(() =>
    this.info.info().simulated
      ? 'Prices are simulated and do not reflect any real market.'
      : `Prices from ${this.info.info().provider} may be delayed.`,
  );

  protected readonly feedDetail = computed(() => {
    const i = this.info.info();
    return i.streaming
      ? `${i.provider}: prices stream as trades happen, with a full check every ${i.pollSeconds}s`
      : `${i.provider}, refreshed about every ${i.pollSeconds}s`;
  });

  constructor() {
    this.live.connect();
    // Frequent enough that the status changes within seconds of the bell. The server enforces it either way.
    timer(0, 20_000)
      .pipe(
        switchMap(() => {
          void this.info.load();
          return this.api.marketStatus().pipe(catchError(() => of(null)));
        }),
        takeUntilDestroyed(),
      )
      .subscribe((status) => {
        this.info.status.set(status);
        this.newYorkNow.set(new Date().toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit', timeZone: MARKET_ZONE }));
      });
    this.live.orderUpdates.pipe(takeUntilDestroyed()).subscribe((o) => this.announce(o));
  }

  ngOnDestroy(): void {
    this.live.disconnect();
  }

  protected openSymbol(symbol: string): void {
    void this.router.navigate(['/trade', symbol]);
  }

  protected signOut(): void {
    this.auth.logout();
  }

  private announce(order: Order): void {
    const verb = order.side === 'Buy' ? 'Bought' : 'Sold';
    if (order.status === 'Filled') {
      this.toasts.show('good', `${verb} ${order.quantity} ${order.symbol} at ${order.filledPrice?.toFixed(2)}`);
    } else if (order.status === 'Rejected') {
      this.toasts.show('bad', `${order.side} ${order.symbol} was rejected. ${order.rejectReason ?? ''}`);
    }
  }
}
