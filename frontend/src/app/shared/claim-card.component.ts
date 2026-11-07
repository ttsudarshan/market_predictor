import { DatePipe } from '@angular/common';
import { Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Claim, ClaimReview } from '../core/models';
import { FORMAT_PIPES } from './format';

/** One creator's call: what they said, a link to the moment they said it, and how it has turned out. */
@Component({
  selector: 'app-claim-card',
  imports: [DatePipe, RouterLink, ...FORMAT_PIPES],
  template: `
    @let c = claim();
    <article class="claim" [class.claim-flagged]="!c.quoteVerified || c.review === 'Incorrect'">
      <header class="claim-head">
        @if (showTicker()) {
          <a class="mono strong" [routerLink]="['/trade', c.ticker]">{{ c.ticker }}</a>
        }
        <span class="direction" [class.gain]="c.direction === 'Bullish'" [class.loss]="c.direction === 'Bearish'">
          {{ c.direction === 'Bullish' ? '▲ Bullish' : '▼ Bearish' }}
        </span>
        @if (c.targetPrice !== null) { <span class="mono">target {{ c.targetPrice | money }}</span> }
        <span class="muted">{{ horizonLabel() }}</span>
        @if (showCreator()) {
          <a class="claim-creator" [routerLink]="['/creators', c.channelId]">{{ c.channelTitle }}</a>
        }
        <span class="muted claim-date">{{ c.madeAt | date: 'MMM d, y' }}</span>
      </header>

      <blockquote>
        “{{ c.quote }}”
        <a class="claim-link" target="_blank" rel="noopener noreferrer" [href]="videoUrl()">Watch at {{ timestamp() }}</a>
        @if (c.speaker) { <span class="muted"> — {{ c.speaker }}</span> }
      </blockquote>
      @if (c.reasoning) { <p class="claim-reason muted">{{ c.reasoning }}</p> }

      <footer class="claim-foot">
        <span class="claim-outcome">
          @switch (c.status) {
            @case ('Final') {
              <span class="tag">Finished</span>
              <span class="mono" [class]="c.returnPercent | pnlClass">{{ c.returnPercent | signedPercent }}</span>
              <span class="muted">call return</span>
              <span class="mono" [class]="c.excessReturnPercent | pnlClass">{{ c.excessReturnPercent | signedPercent }}</span>
              <span class="muted">vs the market</span>
              @if (c.targetHit !== null) { <span class="muted">· target {{ c.targetHit ? 'hit' : 'missed' }}</span> }
            }
            @case ('Pending') {
              <span class="tag">Open until {{ c.resolvesAt | date: 'MMM d' }}</span>
              @if (c.returnPercent !== null) {
                <span class="mono" [class]="c.returnPercent | pnlClass">{{ c.returnPercent | signedPercent }}</span>
                <span class="muted">so far</span>
              }
            }
            @default { <span class="tag">Can't be scored: no prices</span> }
          }
          @if (!c.quoteVerified) { <span class="tag tag-bad">Quote not found in transcript, so this doesn't count</span> }
          @if (c.review === 'Incorrect') { <span class="tag tag-bad">Marked wrong, so this doesn't count</span> }
          @if (c.review === 'Correct') { <span class="tag">Checked</span> }
        </span>
        <span class="claim-actions">
          @if (reviewable()) {
            <button type="button" class="ghost" [attr.aria-pressed]="c.review === 'Correct'" (click)="reviewed.emit(c.review === 'Correct' ? null : 'Correct')">Correct</button>
            <button type="button" class="ghost" [attr.aria-pressed]="c.review === 'Incorrect'" (click)="reviewed.emit(c.review === 'Incorrect' ? null : 'Incorrect')">Wrong</button>
          }
          <a class="chip" [routerLink]="['/trade', c.ticker]" [queryParams]="{ claim: c.id }">Paper trade</a>
        </span>
      </footer>
    </article>
  `,
})
export class ClaimCardComponent {
  readonly claim = input.required<Claim>();
  readonly showTicker = input(true);
  readonly showCreator = input(true);
  readonly reviewable = input(false);
  readonly reviewed = output<ClaimReview | null>();

  protected readonly videoUrl = computed(
    () => `https://www.youtube.com/watch?v=${encodeURIComponent(this.claim().youTubeVideoId)}&t=${this.claim().timestampSeconds}s`,
  );

  protected readonly timestamp = computed(() => {
    const s = this.claim().timestampSeconds;
    const pad = (n: number) => String(n).padStart(2, '0');
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    return h > 0 ? `${h}:${pad(m)}:${pad(s % 60)}` : `${m}:${pad(s % 60)}`;
  });

  protected readonly horizonLabel = computed(() => {
    const c = this.claim();
    return c.horizonStated ? `over ${c.horizonDays} days` : `no timeframe given, scored over ${c.horizonDays} days`;
  });
}
