import { Component, computed, inject, input, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { errorMessage } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { MarketInfoStore } from '../core/market-info.store';
import { FORMAT_PIPES } from '../shared/format';

@Component({
  selector: 'app-auth-page',
  imports: [RouterLink, ...FORMAT_PIPES],
  template: `
    <div class="auth">
      <section class="auth-intro">
        <span class="wordmark">Paperdesk</span>
        <h1>Practice the trade before you pay for the lesson.</h1>
        <p>
          Start with {{ info.info().defaultStartingBalance | money }} of play money, trade real tickers, and see how your
          decisions would have worked out.
        </p>
      </section>

      <form class="auth-form" (submit)="submit($event)">
        <h2>{{ isRegister() ? 'Create your account' : 'Sign in' }}</h2>

        @if (isRegister()) {
          <label>
            Name
            <input #nameBox name="name" autocomplete="name" required maxlength="60" [value]="displayName()" (input)="displayName.set(nameBox.value)" />
          </label>
        }
        <label>
          Email
          <input #emailBox name="email" type="email" autocomplete="email" required [value]="email()" (input)="email.set(emailBox.value)" />
        </label>
        <label>
          Password
          <input
            #passwordBox
            name="password"
            type="password"
            required
            [attr.minlength]="isRegister() ? 8 : null"
            [attr.autocomplete]="isRegister() ? 'new-password' : 'current-password'"
            [value]="password()"
            (input)="password.set(passwordBox.value)"
          />
          @if (isRegister()) { <small class="muted">At least 8 characters</small> }
        </label>

        @if (error(); as message) { <p class="form-error" role="alert">{{ message }}</p> }

        <button type="submit" class="primary" [disabled]="busy()">
          {{ busy() ? 'One moment…' : isRegister() ? 'Create account' : 'Sign in' }}
        </button>

        <p class="auth-switch muted">
          @if (isRegister()) {
            Already have an account? <a routerLink="/login">Sign in</a>
          } @else {
            New here? <a routerLink="/register">Create an account</a>
          }
        </p>
      </form>
    </div>
  `,
})
export class AuthPage {
  /** Bound from the route's data. */
  readonly mode = input<'login' | 'register'>('login');

  protected info = inject(MarketInfoStore);
  private auth = inject(AuthService);
  private router = inject(Router);

  protected readonly isRegister = computed(() => this.mode() === 'register');
  protected readonly displayName = signal('');
  protected readonly email = signal('');
  protected readonly password = signal('');
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);

  protected submit(event: Event): void {
    event.preventDefault();
    this.error.set(null);
    this.busy.set(true);

    const request = this.isRegister()
      ? this.auth.register(this.email(), this.password(), this.displayName())
      : this.auth.login(this.email(), this.password());

    request.subscribe({
      next: () => void this.router.navigateByUrl('/home'),
      error: (err: unknown) => {
        this.error.set(errorMessage(err));
        this.busy.set(false);
      },
    });
  }
}
