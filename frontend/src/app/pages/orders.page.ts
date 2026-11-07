import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { ApiService, errorMessage } from '../core/api.service';
import { LiveService } from '../core/live.service';
import { Fill, Order } from '../core/models';
import { ToastService } from '../core/toast.service';
import { FORMAT_PIPES } from '../shared/format';

type Tab = 'open' | 'all' | 'fills';

@Component({
  selector: 'app-orders-page',
  imports: [RouterLink, ...FORMAT_PIPES],
  template: `
    <header class="page-head"><h1>Orders</h1></header>

    <div class="segmented" role="group" aria-label="Show">
      <button type="button" [class.on]="tab() === 'open'" [attr.aria-pressed]="tab() === 'open'" (click)="tab.set('open')">Open ({{ open().length }})</button>
      <button type="button" [class.on]="tab() === 'all'" [attr.aria-pressed]="tab() === 'all'" (click)="tab.set('all')">All orders</button>
      <button type="button" [class.on]="tab() === 'fills'" [attr.aria-pressed]="tab() === 'fills'" (click)="tab.set('fills')">Trade history</button>
    </div>

    @if (error(); as message) {
      <div class="notice notice-bad" role="alert">{{ message }} <button type="button" class="link" (click)="load()">Try again</button></div>
    }

    @if (loaded()) {
      <section class="panel">
        @if (tab() === 'fills') {
          @if (fills().length === 0) {
            <div class="empty"><p>No trades yet. Filled orders appear here with their realized profit or loss.</p></div>
          } @else {
            <div class="table-wrap">
              <table>
                <thead>
                  <tr><th>Time</th><th>Symbol</th><th>Side</th><th class="num">Qty</th><th class="num">Price</th><th class="num">Value</th><th class="num">Realized P&amp;L</th></tr>
                </thead>
                <tbody>
                  @for (f of fills(); track f.id) {
                    <tr>
                      <td class="nowrap">{{ f.executedAt | marketTime }}</td>
                      <td><a class="mono strong" [routerLink]="['/trade', f.symbol]">{{ f.symbol }}</a></td>
                      <td>{{ f.side }}</td>
                      <td class="num">{{ f.quantity | qty }}</td>
                      <td class="num">{{ f.price | money }}</td>
                      <td class="num">{{ f.quantity * f.price | money }}</td>
                      <td class="num" [class]="f.realizedPnl | pnlClass">{{ f.realizedPnl | signedMoney }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        } @else {
          @if (visibleOrders().length === 0) {
            <div class="empty">
              <p>{{ tab() === 'open' ? 'No open orders. Limit and stop orders wait here until they trigger.' : 'No orders yet.' }}</p>
            </div>
          } @else {
            <div class="table-wrap">
              <table>
                <thead>
                  <tr><th>Placed</th><th>Symbol</th><th>Order</th><th class="num">Qty</th><th class="num">Trigger</th><th class="num">Filled at</th><th>Status</th><th></th></tr>
                </thead>
                <tbody>
                  @for (o of visibleOrders(); track o.id) {
                    <tr>
                      <td class="nowrap">{{ o.createdAt | marketTime }}</td>
                      <td><a class="mono strong" [routerLink]="['/trade', o.symbol]">{{ o.symbol }}</a></td>
                      <td>{{ o.side }} {{ o.type.toLowerCase() }}</td>
                      <td class="num">{{ o.quantity | qty }}</td>
                      <td class="num">{{ o.limitPrice ?? o.stopPrice | money }}</td>
                      <td class="num">{{ o.filledPrice | money }}</td>
                      <td>
                        <span class="tag" [class.tag-open]="o.status === 'Filled'" [class.tag-bad]="o.status === 'Rejected'" [attr.title]="o.rejectReason">{{ o.status }}</span>
                        @if (o.rejectReason) { <span class="cell-sub">{{ o.rejectReason }}</span> }
                      </td>
                      <td class="num">
                        @if (o.status === 'Open') { <button type="button" class="ghost" (click)="cancel(o)">Cancel</button> }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        }
      </section>
    } @else if (!error()) {
      <p class="muted loading">Loading your orders…</p>
    }
  `,
})
export class OrdersPage {
  private api = inject(ApiService);
  private live = inject(LiveService);
  private toasts = inject(ToastService);

  protected readonly tab = signal<Tab>('open');
  protected readonly orders = signal<Order[]>([]);
  protected readonly fills = signal<Fill[]>([]);
  protected readonly loaded = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly open = computed(() => this.orders().filter((o) => o.status === 'Open'));
  protected readonly visibleOrders = computed(() => (this.tab() === 'open' ? this.open() : this.orders()));

  constructor() {
    this.load();
    this.live.orderUpdates.pipe(takeUntilDestroyed()).subscribe(() => this.load());
  }

  protected load(): void {
    forkJoin({ orders: this.api.orders(), fills: this.api.fills() }).subscribe({
      next: ({ orders, fills }) => {
        this.orders.set(orders);
        this.fills.set(fills);
        this.loaded.set(true);
        this.error.set(null);
      },
      error: (err: unknown) => this.error.set(errorMessage(err)),
    });
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
