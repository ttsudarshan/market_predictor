import { Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ApiService, errorMessage } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { MarketInfoStore } from '../core/market-info.store';
import { ToastService } from '../core/toast.service';
import { FORMAT_PIPES } from '../shared/format';

const MIN = 1000;
const MAX = 100000000;

@Component({
  selector: 'app-account-page',
  imports: [...FORMAT_PIPES],
  template: `
    <header class="page-head"><h1>Account</h1></header>

    <section class="panel">
      <h2>Profile</h2>
      <dl class="stats stats-wide">
        <div><dt>Name</dt><dd>{{ auth.user()?.displayName }}</dd></div>
        <div><dt>Email</dt><dd>{{ auth.user()?.email }}</dd></div>
      </dl>
    </section>

    <section class="panel">
      <h2>Market data and rules</h2>
      <dl class="stats stats-wide">
        <div><dt>Price source</dt><dd>{{ info.info().provider }}</dd></div>
        <div><dt>Updates</dt><dd>{{ info.info().streaming ? 'Live, as trades happen' : 'Every ' + info.info().pollSeconds + ' seconds' }}</dd></div>
        <div><dt>Account currency</dt><dd>{{ info.info().baseCurrency }}</dd></div>
        <div><dt>Commission per order</dt><dd>{{ info.info().commissionPerOrder | money }}</dd></div>
      </dl>
      <p class="muted small">
        Orders fill at the last traded price, with no slippage or partial fills, and only in the regular session: 9:30 AM to
        4:00 PM New York time on trading days. While the market is closed, market orders are refused, and limit and stop
        orders wait for the open. Stocks and ETFs only, with no shorting or margin.
      </p>
    </section>

    <section class="panel">
      <h2>Reset account</h2>
      <p>Deletes all your positions, orders and trade history, then restarts with a new balance. Your watchlist stays. This can't be undone.</p>
      <form class="reset-form" (submit)="reset($event)">
        <label>
          New starting balance
          <input #balanceBox class="mono" type="number" [min]="min" [max]="max" step="1000" [value]="balance()" (input)="balance.set(balanceBox.value); confirming.set(false)" />
        </label>
        @if (confirming()) {
          <button type="submit" class="danger" [disabled]="busy()">Yes, delete my history</button>
          <button type="button" class="ghost" (click)="confirming.set(false)">Keep my account</button>
        } @else {
          <button type="submit" class="ghost-border" [disabled]="!valid()">Reset account…</button>
        }
      </form>
      @if (!valid()) { <p class="form-error">Enter an amount from {{ min | money }} to {{ max | money }}.</p> }
      @if (error(); as message) { <p class="form-error" role="alert">{{ message }}</p> }
    </section>
  `,
})
export class AccountPage {
  protected auth = inject(AuthService);
  protected info = inject(MarketInfoStore);
  private api = inject(ApiService);
  private router = inject(Router);
  private toasts = inject(ToastService);

  protected readonly min = MIN;
  protected readonly max = MAX;
  protected readonly balance = signal(String(this.info.info().defaultStartingBalance));
  protected readonly confirming = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly valid = computed(() => {
    const value = Number(this.balance());
    return value >= MIN && value <= MAX;
  });

  protected reset(event: Event): void {
    event.preventDefault();
    if (!this.valid()) return;
    if (!this.confirming()) {
      this.confirming.set(true);
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.api.resetAccount(Number(this.balance())).subscribe({
      next: () => {
        this.toasts.show('info', 'Your account has been reset.');
        void this.router.navigateByUrl('/portfolio');
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.confirming.set(false);
        this.error.set(errorMessage(err));
      },
    });
  }
}
