import { DatePipe } from '@angular/common';
import { Component, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { catchError, debounceTime, filter, merge, of, switchMap } from 'rxjs';
import { ApiService, errorMessage } from '../core/api.service';
import { LiveService } from '../core/live.service';
import { ChannelDetail, Claim, ClaimReview, Video, VideoStatus } from '../core/models';
import { ToastService } from '../core/toast.service';
import { ClaimCardComponent } from '../shared/claim-card.component';
import { FORMAT_PIPES } from '../shared/format';

type Tab = 'calls' | 'videos' | 'changes';

const STATUS_LABEL: Record<VideoStatus, string> = {
  Discovered: 'Waiting for transcript',
  TranscriptFetched: 'Waiting to be read',
  Extracted: 'Waiting to be scored',
  Scored: 'Done',
  NoCaptions: 'No captions',
  Failed: 'Failed',
};

@Component({
  selector: 'app-creator-detail-page',
  imports: [DatePipe, RouterLink, ClaimCardComponent, ...FORMAT_PIPES],
  template: `
    @if (error(); as message) { <div class="notice notice-bad" role="alert">{{ message }}</div> }

    @if (detail(); as d) {
      <header class="page-head">
        <div>
          <p class="eyebrow"><a routerLink="/creators">Creators</a></p>
          <h1>{{ d.channel.title }}</h1>
          <p class="muted">
            <a target="_blank" rel="noopener noreferrer" [href]="'https://www.youtube.com/channel/' + d.channel.youTubeChannelId">Open on YouTube</a>
          </p>
        </div>
        <div class="head-actions">
          @if (d.channel.counts.noCaptions + d.channel.counts.failed > 0) {
            <button type="button" class="ghost-border" (click)="retry()">Retry {{ d.channel.counts.noCaptions + d.channel.counts.failed }} videos</button>
          }
          <button type="button" class="ghost-border" (click)="toggleFollow()">{{ d.channel.following ? 'Unfollow' : 'Follow' }}</button>
        </div>
      </header>

      <section class="panel">
        <dl class="stats stats-wide">
          <div><dt>Rank</dt><dd class="mono">{{ d.channel.stats.rank ? '#' + d.channel.stats.rank : 'Not ranked yet' }}</dd></div>
          <div>
            <dt>vs the market</dt>
            <dd class="mono" [class]="d.channel.stats.averageExcessReturn | pnlClass">{{ d.channel.stats.averageExcessReturn | signedPercent }}</dd>
          </div>
          <div><dt>Direction right</dt><dd class="mono">{{ rate(d.channel.stats.hitRate) }}</dd></div>
          <div><dt>Beat the market</dt><dd class="mono">{{ rate(d.channel.stats.beatBenchmarkRate) }}</dd></div>
          <div><dt>Targets hit</dt><dd class="mono">{{ rate(d.channel.stats.targetHitRate) }}</dd></div>
          <div><dt>Finished calls</dt><dd class="mono">{{ d.channel.stats.finalCalls }}</dd></div>
          <div><dt>Open calls</dt><dd class="mono">{{ d.channel.stats.pendingCalls }}</dd></div>
        </dl>
      </section>

      <div class="segmented" role="group" aria-label="Show">
        <button type="button" [class.on]="tab() === 'calls'" [attr.aria-pressed]="tab() === 'calls'" (click)="tab.set('calls')">Calls ({{ d.claims.length }})</button>
        <button type="button" [class.on]="tab() === 'videos'" [attr.aria-pressed]="tab() === 'videos'" (click)="tab.set('videos')">Videos ({{ d.videos.length }})</button>
        <button type="button" [class.on]="tab() === 'changes'" [attr.aria-pressed]="tab() === 'changes'" (click)="tab.set('changes')">Changed mind ({{ d.reversals.length }})</button>
      </div>

      <section class="panel">
        @switch (tab()) {
          @case ('calls') {
            @if (d.claims.length === 0) {
              <div class="empty"><p>No calls yet. They appear here as videos are read.</p></div>
            } @else {
              <div class="claim-list">
                @for (c of d.claims; track c.id) {
                  <app-claim-card [claim]="c" [showCreator]="false" [reviewable]="true" (reviewed)="review(c, $event)" />
                }
              </div>
            }
          }
          @case ('videos') {
            <div class="table-wrap">
              <table>
                <thead><tr><th>Published</th><th>Video</th><th>Status</th><th class="num">Calls</th></tr></thead>
                <tbody>
                  @for (v of d.videos; track v.id) {
                    <tr>
                      <td class="nowrap">{{ v.publishedAt ? (v.publishedAt | date: 'MMM d, y') : '—' }}</td>
                      <td><a target="_blank" rel="noopener noreferrer" [href]="'https://www.youtube.com/watch?v=' + v.youTubeVideoId">{{ v.title }}</a></td>
                      <td>
                        <span class="tag" [class.tag-open]="v.status === 'Scored'" [class.tag-bad]="v.status === 'Failed'">{{ label(v) }}</span>
                        @if (v.lastError) { <span class="cell-sub">{{ v.lastError }}</span> }
                      </td>
                      <td class="num">{{ v.claimCount }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
          @default {
            @if (d.reversals.length === 0) {
              <div class="empty"><p>No reversals: this creator hasn't flipped on a ticker within 30 days.</p></div>
            } @else {
              <div class="table-wrap">
                <table>
                  <thead><tr><th>Ticker</th><th>First said</th><th>Then said</th><th class="num">Days apart</th></tr></thead>
                  <tbody>
                    @for (r of d.reversals; track r.ticker + r.secondAt) {
                      <tr>
                        <td><a class="mono strong" [routerLink]="['/trade', r.ticker]">{{ r.ticker }}</a></td>
                        <td>{{ r.firstDirection }} · {{ r.firstAt | date: 'MMM d, y' }}</td>
                        <td>{{ r.secondDirection }} · {{ r.secondAt | date: 'MMM d, y' }}</td>
                        <td class="num">{{ r.daysApart }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          }
        }
      </section>
    } @else if (!error()) {
      <p class="muted loading">Loading…</p>
    }
  `,
})
export class CreatorDetailPage {
  /** Bound from the route. */
  readonly id = input.required<string>();

  private api = inject(ApiService);
  private live = inject(LiveService);
  private toasts = inject(ToastService);

  protected readonly tab = signal<Tab>('calls');
  protected readonly detail = signal<ChannelDetail | null>(null);
  protected readonly error = signal<string | null>(null);
  private readonly channelId = computed(() => this.id());

  constructor() {
    const ownUpdates = this.live.pipelineUpdates.pipe(filter((id) => id === this.channelId()), debounceTime(800));
    merge(toObservable(this.channelId), ownUpdates)
      .pipe(
        switchMap(() =>
          this.api.channel(this.channelId()).pipe(
            catchError((err: unknown) => {
              this.error.set(errorMessage(err));
              return of(null);
            }),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((d) => {
        if (d) {
          this.detail.set(d);
          this.error.set(null);
        }
      });
  }

  protected label(v: Video): string {
    return STATUS_LABEL[v.status];
  }

  protected rate(value: number | null): string {
    return value === null ? '—' : `${value}%`;
  }

  protected review(claim: Claim, review: ClaimReview | null): void {
    this.api.reviewClaim(claim.id, review).subscribe({
      next: () => this.reload(),
      error: (err: unknown) => this.toasts.show('bad', errorMessage(err)),
    });
  }

  protected toggleFollow(): void {
    const following = this.detail()?.channel.following;
    const request = following ? this.api.unfollowChannel(this.channelId()) : this.api.followChannel(this.channelId());
    request.subscribe({
      next: () => this.reload(),
      error: (err: unknown) => this.toasts.show('bad', errorMessage(err)),
    });
  }

  protected retry(): void {
    this.api.retryChannel(this.channelId()).subscribe({
      next: (r) => {
        this.toasts.show('info', `Queued ${r.retried} videos to try again.`);
        this.reload();
      },
      error: (err: unknown) => this.toasts.show('bad', errorMessage(err)),
    });
  }

  private reload(): void {
    this.api.channel(this.channelId()).subscribe((d) => this.detail.set(d));
  }
}
