import { DecimalPipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { debounceTime, forkJoin } from 'rxjs';
import { ApiService, errorMessage } from '../core/api.service';
import { LiveService } from '../core/live.service';
import { Accuracy, Channel, CreatorStats, PipelineStatus } from '../core/models';
import { ToastService } from '../core/toast.service';
import { FORMAT_PIPES } from '../shared/format';

@Component({
  selector: 'app-creators-page',
  imports: [DecimalPipe, RouterLink, ...FORMAT_PIPES],
  template: `
    <header class="page-head"><h1>Creators</h1></header>

    <section class="panel">
      <h2>Track a YouTube channel</h2>
      <form class="add-channel" (submit)="add($event)">
        <label class="add-channel-link">
          Channel link
          <input #linkBox placeholder="youtube.com/@channelname" autocomplete="off" spellcheck="false" [value]="link()" (input)="link.set(linkBox.value)" />
        </label>
        <label>
          Past videos
          <select #countBox [value]="backfill()" (change)="backfill.set(+countBox.value)">
            <option value="5">Last 5 (trial)</option>
            <option value="30">Last 30</option>
            <option value="60">Last 60</option>
          </select>
        </label>
        <button type="submit" class="primary" [disabled]="adding() || !link().trim()">{{ adding() ? 'Finding channel…' : 'Add channel' }}</button>
      </form>
      @if (addError(); as message) { <p class="form-error" role="alert">{{ message }}</p> }

      @if (status(); as s) {
        <p class="notice" [class.notice-bad]="!s.running">
          {{ s.running ? 'Fetching and processing videos.' : 'Stopped. Nothing is fetched until you start it.' }}
          <button type="button" [class.primary]="!s.running" [disabled]="toggling()" (click)="togglePipeline(s.running)">
            {{ s.running ? 'Stop' : 'Start' }}
          </button>
        </p>
        @if (!s.llmConfigured) {
          <p class="notice">
            Transcripts are being collected, but predictions can't be read until you add an API key.
            Set <code>GEMINI_API_KEY</code> or <code>ANTHROPIC_API_KEY</code> and restart the server, and processing picks up from here.
          </p>
        }
        @if (s.llmPausedReason) { <p class="notice notice-bad">Reading predictions is paused: {{ s.llmPausedReason }}</p> }
        @if (s.youTubePausedReason) { <p class="notice notice-bad">Fetching from YouTube is paused for a while: {{ s.youTubePausedReason }}</p> }
      }
    </section>

    @if (error(); as message) {
      <div class="notice notice-bad" role="alert">{{ message }} <button type="button" class="link" (click)="load()">Try again</button></div>
    }

    @if (channels().length) {
      <section class="panel">
        <h2>Your channels</h2>
        <div class="table-wrap">
          <table>
            <thead><tr><th>Channel</th><th>Progress</th><th class="num">Videos read</th><th class="num">Calls found</th><th class="hide-narrow">Problems</th></tr></thead>
            <tbody>
              @for (c of channels(); track c.id) {
                <tr>
                  <td><a class="strong" [routerLink]="['/creators', c.id]">{{ c.title }}</a></td>
                  <td class="progress-cell">
                    <div class="progress" role="progressbar" [attr.aria-valuenow]="percentDone(c)" aria-valuemin="0" aria-valuemax="100">
                      <span [style.width.%]="percentDone(c)"></span>
                    </div>
                    <span class="cell-sub">{{ stageLabel(c) }}</span>
                  </td>
                  <td class="num">{{ c.stats.videosProcessed }} of {{ c.counts.total }}</td>
                  <td class="num">{{ c.stats.totalCalls }}</td>
                  <td class="hide-narrow">
                    @if (c.counts.noCaptions) { <span class="cell-sub">{{ c.counts.noCaptions }} with no captions</span> }
                    @if (c.counts.failed) { <span class="cell-sub">{{ c.counts.failed }} failed</span> }
                    @if (c.lastPollError) { <span class="cell-sub">{{ c.lastPollError }}</span> }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </section>
    }

    <section class="panel">
      <h2>Leaderboard</h2>
      @if (leaderboard().length === 0) {
        <div class="empty"><p>Add a channel above. Its record appears here as its videos are read and its calls reach their dates.</p></div>
      } @else {
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th class="num rank-col">Rank</th><th>Creator</th>
                <th class="num">vs market</th><th class="num">Avg return</th><th class="num">Direction right</th><th class="num">Beat market</th><th class="num">Targets hit</th>
                <th class="num">Finished calls</th><th class="num">Open</th>
              </tr>
            </thead>
            <tbody>
              @for (r of leaderboard(); track r.channelId) {
                <tr>
                  <td class="num rank-col">{{ r.rank ?? '–' }}</td>
                  <td><a class="strong" [routerLink]="['/creators', r.channelId]">{{ r.title }}</a></td>
                  <td class="num" [class]="r.averageExcessReturn | pnlClass">{{ r.averageExcessReturn | signedPercent }}</td>
                  <td class="num" [class]="r.averageReturn | pnlClass">{{ r.averageReturn | signedPercent }}</td>
                  <td class="num">{{ rate(r.hitRate) }}</td>
                  <td class="num">{{ rate(r.beatBenchmarkRate) }}</td>
                  <td class="num">{{ rate(r.targetHitRate) }}</td>
                  <td class="num">{{ r.finalCalls }}</td>
                  <td class="num">{{ r.pendingCalls }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        @if (status(); as s) {
          <p class="small muted table-note">
            Ranked by average return compared with {{ s.benchmark }} over the same dates, so riding a rising market doesn't count as skill.
            A creator needs {{ s.minCallsToRank }} finished calls to be ranked. A call with no timeframe is scored over {{ s.defaultHorizonDays }} days and counts half.
            Repeating an open call counts once.
          </p>
        }
      }
    </section>

    @if (accuracy(); as a) {
      @if (a.totalClaims > 0) {
        <section class="panel">
          <h2>How accurate is the extraction?</h2>
          <dl class="stats stats-wide">
            <div><dt>Calls extracted</dt><dd class="mono">{{ a.totalClaims }}</dd></div>
            <div><dt>Checked by you</dt><dd class="mono">{{ a.reviewed }}</dd></div>
            <div><dt>Correct when checked</dt><dd class="mono">{{ a.accuracy === null ? '—' : a.accuracy + '%' }}</dd></div>
            <div><dt>Quotes not in transcript</dt><dd class="mono">{{ a.unverifiedQuotes }}</dd></div>
            @if (status(); as s) {
              <div><dt>Tokens used ({{ s.model }})</dt><dd class="mono">{{ s.inputTokens + s.outputTokens | number }}</dd></div>
            }
          </dl>
          <p class="small muted table-note">Open a creator and mark calls Correct or Wrong to measure this. Calls marked Wrong stop counting towards rankings.</p>
        </section>
      }
    }
  `,
})
export class CreatorsPage {
  private api = inject(ApiService);
  private live = inject(LiveService);
  private toasts = inject(ToastService);

  protected readonly link = signal('');
  protected readonly backfill = signal(60);
  protected readonly adding = signal(false);
  protected readonly addError = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly channels = signal<Channel[]>([]);
  protected readonly leaderboard = signal<CreatorStats[]>([]);
  protected readonly status = signal<PipelineStatus | null>(null);
  protected readonly accuracy = signal<Accuracy | null>(null);
  protected readonly toggling = signal(false);

  constructor() {
    this.load();
    this.live.pipelineUpdates.pipe(debounceTime(800), takeUntilDestroyed()).subscribe(() => this.load());
  }

  protected load(): void {
    forkJoin({
      channels: this.api.channels(),
      leaderboard: this.api.leaderboard(),
      status: this.api.pipelineStatus(),
      accuracy: this.api.accuracy(),
    }).subscribe({
      next: (r) => {
        this.channels.set(r.channels);
        this.leaderboard.set(r.leaderboard);
        this.status.set(r.status);
        this.accuracy.set(r.accuracy);
        this.error.set(null);
      },
      error: (err: unknown) => this.error.set(errorMessage(err)),
    });
  }

  protected togglePipeline(running: boolean): void {
    this.toggling.set(true);
    (running ? this.api.stopPipeline() : this.api.startPipeline()).subscribe({
      next: () => {
        this.toggling.set(false);
        this.load();
      },
      error: (err: unknown) => {
        this.toggling.set(false);
        this.toasts.show('bad', errorMessage(err));
      },
    });
  }

  protected add(event: Event): void {
    event.preventDefault();
    this.adding.set(true);
    this.addError.set(null);
    this.api.addChannel(this.link().trim(), this.backfill()).subscribe({
      next: (channel) => {
        this.adding.set(false);
        this.link.set('');
        this.toasts.show('good', `Added ${channel.title}. Its videos will appear below within a minute.`);
        this.load();
      },
      error: (err: unknown) => {
        this.adding.set(false);
        this.addError.set(errorMessage(err));
      },
    });
  }

  protected percentDone(c: Channel): number {
    const { total, extracted, scored, noCaptions, failed } = c.counts;
    return total === 0 ? 0 : Math.round(((extracted + scored + noCaptions + failed) / total) * 100);
  }

  protected stageLabel(c: Channel): string {
    const { total, waiting, transcriptFetched, extracted } = c.counts;
    if (total === 0) return 'Finding videos…';
    if (waiting > 0) return `Fetching transcripts, ${waiting} to go`;
    if (transcriptFetched > 0) return this.status()?.llmConfigured ? `Reading videos, ${transcriptFetched} to go` : `${transcriptFetched} waiting for an API key`;
    if (extracted > 0) return 'Scoring against prices…';
    return 'Up to date';
  }

  protected rate(value: number | null): string {
    return value === null ? '—' : `${value}%`;
  }
}
