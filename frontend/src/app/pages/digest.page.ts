import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { debounceTime, forkJoin } from 'rxjs';
import { ApiService, errorMessage } from '../core/api.service';
import { LiveService } from '../core/live.service';
import { AskResult, HeadsUp, InboxDay, InboxItem, PipelineStatus } from '../core/models';
import { ClaimCardComponent } from '../shared/claim-card.component';

@Component({
  selector: 'app-digest-page',
  imports: [DatePipe, RouterLink, ClaimCardComponent],
  template: `
    <header class="page-head"><h1>Digest</h1></header>

    @if (error(); as message) {
      <div class="notice notice-bad" role="alert">{{ message }} <button type="button" class="link" (click)="load()">Try again</button></div>
    }

    <section class="panel">
      <div class="panel-head">
        <h2>Market heads-up</h2>
        <label class="check">
          <input type="checkbox" [checked]="followedOnly()" (change)="followedOnly.set(!followedOnly()); load()" />
          Only creators I follow
        </label>
      </div>

      @if (headsUp(); as h) {
        <p class="notice">{{ h.disclaimer }}</p>
        @if (h.brief) {
          <p class="brief">{{ h.brief }}</p>
          <p class="small muted">Written {{ h.briefDate | date: 'MMM d' }} from all tracked creators.</p>
        }

        @if (h.tickers.length === 0) {
          <div class="empty"><p>No open calls from the last {{ h.windowDays }} days yet.</p></div>
        } @else {
          <div class="table-wrap">
            <table>
              <thead><tr><th>Ticker</th><th>Sentiment</th><th class="num">Bullish</th><th class="num">Bearish</th><th class="hide-narrow">Who</th></tr></thead>
              <tbody>
                @for (r of h.tickers; track r.ticker) {
                  <tr>
                    <td><a class="mono strong" [routerLink]="['/trade', r.ticker]">{{ r.ticker }}</a></td>
                    <td class="sentiment-cell">
                      <div class="meter" [attr.aria-label]="sentimentLabel(r.netSentiment)" role="img">
                        <span class="meter-bar" [class.meter-gain]="r.netSentiment > 0" [class.meter-loss]="r.netSentiment < 0"
                          [style.width.%]="abs(r.netSentiment) * 50"
                          [style.left.%]="r.netSentiment >= 0 ? 50 : 50 - abs(r.netSentiment) * 50"></span>
                      </div>
                      <span class="cell-sub">{{ sentimentLabel(r.netSentiment) }}</span>
                    </td>
                    <td class="num">{{ r.bullishCalls }}</td>
                    <td class="num">{{ r.bearishCalls }}</td>
                    <td class="hide-narrow">
                      @if (r.bullishCreators.length) { <span class="cell-sub">▲ {{ r.bullishCreators.join(', ') }}</span> }
                      @if (r.bearishCreators.length) { <span class="cell-sub">▼ {{ r.bearishCreators.join(', ') }}</span> }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <p class="small muted table-note">
            Open calls from the last {{ h.windowDays }} days. Each creator's latest view counts once, and creators with better records count for more.
          </p>
        }
      }
    </section>

    <section class="panel">
      <h2>Ask about the calls</h2>
      <form class="ask" (submit)="ask($event)">
        <input #askBox aria-label="Your question" placeholder="Who has been right about NVDA?" maxlength="500" [value]="question()" (input)="question.set(askBox.value)" />
        <button type="submit" class="primary" [disabled]="asking() || question().trim().length < 3 || !canAsk()">{{ asking() ? 'Thinking…' : 'Ask' }}</button>
      </form>
      @if (!canAsk()) { <p class="small muted">Add an API key to ask questions.</p> }
      @if (askError(); as message) { <p class="form-error" role="alert">{{ message }}</p> }
      @if (answer(); as a) {
        <div class="answer">
          <p>{{ a.answer }}</p>
          <p class="small muted">Answered only from stored calls. {{ a.disclaimer }}</p>
          @if (a.citations.length) {
            <div class="claim-list">
              @for (c of a.citations; track c.id) { <app-claim-card [claim]="c" /> }
            </div>
          }
        </div>
      }
    </section>

    <section class="panel">
      <h2>New calls from creators you follow</h2>
      @if (days().length === 0) {
        <div class="empty">
          <p>Nothing new in the last 14 days.</p>
          <a class="chip" routerLink="/creators">Add a creator</a>
        </div>
      }
      @for (day of days(); track day.key) {
        <h3 class="day-head">{{ day.date | date: 'EEEE, MMM d' }}</h3>
        <div class="claim-list">
          @for (item of day.items; track item.claim.id) {
            <div class="inbox-item">
              @if (item.reasons.length) {
                <p class="reasons">
                  @if (item.worthWatching) { <span class="tag tag-open">Worth watching</span> }
                  @for (r of item.reasons; track r) { <span class="small muted">{{ r }}</span> }
                </p>
              }
              <app-claim-card [claim]="item.claim" />
            </div>
          }
        </div>
      }
    </section>
  `,
})
export class DigestPage {
  private api = inject(ApiService);
  private live = inject(LiveService);

  protected readonly followedOnly = signal(false);
  protected readonly headsUp = signal<HeadsUp | null>(null);
  protected readonly inbox = signal<InboxDay[]>([]);
  protected readonly status = signal<PipelineStatus | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly question = signal('');
  protected readonly asking = signal(false);
  protected readonly askError = signal<string | null>(null);
  protected readonly answer = signal<AskResult | null>(null);
  protected readonly canAsk = computed(() => this.status()?.llmConfigured ?? true);

  /** Grouped by the viewer's own calendar day, so headings match the dates on the calls beneath them. */
  protected readonly days = computed(() => {
    const groups = new Map<string, { key: string; date: Date; items: InboxItem[] }>();
    for (const item of this.inbox().flatMap((d) => d.items)) {
      const date = new Date(item.claim.madeAt);
      const key = date.toDateString();
      if (!groups.has(key)) groups.set(key, { key, date, items: [] });
      groups.get(key)!.items.push(item);
    }
    return [...groups.values()]
      .sort((a, b) => b.date.getTime() - a.date.getTime())
      .map((g) => ({ ...g, items: [...g.items].sort((a, b) => b.reasons.length - a.reasons.length) }));
  });

  constructor() {
    this.load();
    this.live.pipelineUpdates.pipe(debounceTime(2000), takeUntilDestroyed()).subscribe(() => this.load());
  }

  protected load(): void {
    forkJoin({
      headsUp: this.api.headsUp(this.followedOnly()),
      inbox: this.api.inbox(14),
      status: this.api.pipelineStatus(),
    }).subscribe({
      next: (r) => {
        this.headsUp.set(r.headsUp);
        this.inbox.set(r.inbox);
        this.status.set(r.status);
        this.error.set(null);
      },
      error: (err: unknown) => this.error.set(errorMessage(err)),
    });
  }

  protected ask(event: Event): void {
    event.preventDefault();
    this.asking.set(true);
    this.askError.set(null);
    this.answer.set(null);
    this.api.ask(this.question().trim()).subscribe({
      next: (a) => {
        this.asking.set(false);
        this.answer.set(a);
      },
      error: (err: unknown) => {
        this.asking.set(false);
        this.askError.set(errorMessage(err));
      },
    });
  }

  protected abs(value: number): number {
    return Math.abs(value);
  }

  protected sentimentLabel(net: number): string {
    if (Math.abs(net) < 0.05) return 'Split';
    const strength = Math.abs(net) >= 0.6 ? 'Mostly' : 'Leaning';
    return `${strength} ${net > 0 ? 'bullish' : 'bearish'}`;
  }
}
