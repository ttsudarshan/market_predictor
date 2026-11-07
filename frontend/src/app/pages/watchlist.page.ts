import { Component, computed, effect, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiService, errorMessage } from '../core/api.service';
import { LiveService } from '../core/live.service';
import { WatchList } from '../core/models';
import { ToastService } from '../core/toast.service';
import { FlashDirective } from '../shared/flash.directive';
import { FORMAT_PIPES } from '../shared/format';
import { SymbolSearchComponent } from '../shared/symbol-search.component';

@Component({
  selector: 'app-watchlist-page',
  imports: [RouterLink, SymbolSearchComponent, FlashDirective, ...FORMAT_PIPES],
  template: `
    <header class="page-head">
      <h1>Lists</h1>
      <app-symbol-search [placeholder]="'Add to ' + (selected()?.name ?? 'list')" (picked)="add($event)" />
    </header>

    @if (error(); as message) {
      <div class="notice notice-bad" role="alert">{{ message }} <button type="button" class="link" (click)="load()">Try again</button></div>
    }

    @if (loaded()) {
      <div class="list-tabs">
        <div class="segmented" role="group" aria-label="Lists">
          @for (l of lists(); track l.id) {
            <button type="button" [class.on]="l.id === selected()?.id" [attr.aria-pressed]="l.id === selected()?.id" (click)="selectedId.set(l.id); editing.set(false)">
              {{ l.isDefault ? '★ ' : '' }}{{ l.name }} ({{ l.symbols.length }})
            </button>
          }
        </div>

        <form class="inline-form" (submit)="create($event)">
          <input #nameBox aria-label="New list name" placeholder="New list" maxlength="40" [value]="newName()" (input)="newName.set(nameBox.value)" />
          <button type="submit" class="ghost-border" [disabled]="!newName().trim()">Create</button>
        </form>

        @if (selected(); as s) {
          @if (editing()) {
            <form class="inline-form" (submit)="rename($event)">
              <input #renameBox aria-label="List name" maxlength="40" [value]="renameText()" (input)="renameText.set(renameBox.value)" />
              <button type="submit" class="ghost-border" [disabled]="!renameText().trim()">Save</button>
              <button type="button" class="ghost" (click)="editing.set(false)">Cancel</button>
            </form>
          } @else {
            <button type="button" class="ghost" (click)="startRename(s)">Rename</button>
            @if (!s.isDefault) { <button type="button" class="ghost" (click)="remove(s)">Delete list</button> }
          }
        }
      </div>

      <section class="panel">
        @if (rows().length === 0) {
          <div class="empty"><p>Nothing here yet. Add a ticker above to follow its price.</p></div>
        } @else {
          <div class="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Symbol</th>
                  <th class="num">Last</th>
                  <th class="num">Change</th>
                  <th class="num">Change %</th>
                  <th class="num hide-narrow">Day low</th>
                  <th class="num hide-narrow">Day high</th>
                  <th class="num hide-narrow">Volume</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (r of rows(); track r.symbol) {
                  <tr class="row-link" tabindex="0" (click)="open(r.symbol)" (keydown.enter)="open(r.symbol)">
                    <td>
                      <a class="mono strong" [routerLink]="['/trade', r.symbol]">{{ r.symbol }}</a>
                      <span class="cell-sub">{{ r.quote?.name ?? '…' }}</span>
                    </td>
                    <td class="num" [appFlash]="r.quote?.price">{{ r.quote?.price | money: r.quote?.currency }}</td>
                    <td class="num" [class]="r.quote?.change | pnlClass">{{ r.quote?.change | signedMoney: r.quote?.currency }}</td>
                    <td class="num" [class]="r.quote?.changePercent | pnlClass">{{ r.quote?.changePercent | signedPercent }}</td>
                    <td class="num hide-narrow">{{ r.quote?.dayLow | money: r.quote?.currency }}</td>
                    <td class="num hide-narrow">{{ r.quote?.dayHigh | money: r.quote?.currency }}</td>
                    <td class="num hide-narrow">{{ r.quote?.volume | compact }}</td>
                    <td class="num">
                      <button type="button" class="ghost" [attr.aria-label]="'Remove ' + r.symbol" (click)="removeSymbol(r.symbol); $event.stopPropagation()" (keydown.enter)="$event.stopPropagation()">Remove</button>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>
    } @else if (!error()) {
      <p class="muted loading">Loading your lists…</p>
    }
  `,
})
export class WatchlistPage {
  private api = inject(ApiService);
  private live = inject(LiveService);
  private router = inject(Router);
  private toasts = inject(ToastService);

  protected readonly lists = signal<WatchList[]>([]);
  protected readonly selectedId = signal<string | null>(null);
  protected readonly loaded = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly newName = signal('');
  protected readonly editing = signal(false);
  protected readonly renameText = signal('');

  protected readonly selected = computed(
    () => this.lists().find((l) => l.id === this.selectedId()) ?? this.lists()[0] ?? null,
  );
  protected readonly rows = computed(() => {
    const quotes = this.live.quotes();
    return (this.selected()?.symbols ?? []).map((symbol) => ({ symbol, quote: quotes[symbol] ?? null }));
  });

  constructor() {
    this.load();
    effect((onCleanup) => {
      const symbols = this.selected()?.symbols ?? [];
      if (symbols.length) onCleanup(this.live.watch(symbols));
    });
  }

  protected load(): void {
    this.api.watchLists().subscribe({
      next: (lists) => {
        this.lists.set(lists);
        this.loaded.set(true);
        this.error.set(null);
      },
      error: (err: unknown) => this.error.set(errorMessage(err)),
    });
  }

  protected add(symbol: string): void {
    const list = this.selected();
    if (!list) return;
    this.api.addToWatchList(list.id, symbol).subscribe({
      next: () => this.load(),
      error: (err: unknown) => this.toasts.show('bad', errorMessage(err)),
    });
  }

  protected removeSymbol(symbol: string): void {
    const list = this.selected();
    if (!list) return;
    this.api.removeFromWatchList(list.id, symbol).subscribe({
      next: () => this.load(),
      error: (err: unknown) => this.toasts.show('bad', errorMessage(err)),
    });
  }

  protected create(event: Event): void {
    event.preventDefault();
    this.api.createWatchList(this.newName().trim()).subscribe({
      next: (list) => {
        this.newName.set('');
        this.selectedId.set(list.id);
        this.load();
      },
      error: (err: unknown) => this.toasts.show('bad', errorMessage(err)),
    });
  }

  protected startRename(list: WatchList): void {
    this.renameText.set(list.name);
    this.editing.set(true);
  }

  protected rename(event: Event): void {
    event.preventDefault();
    const list = this.selected();
    if (!list) return;
    this.api.renameWatchList(list.id, this.renameText().trim()).subscribe({
      next: () => {
        this.editing.set(false);
        this.load();
      },
      error: (err: unknown) => this.toasts.show('bad', errorMessage(err)),
    });
  }

  protected remove(list: WatchList): void {
    this.api.deleteWatchList(list.id).subscribe({
      next: () => {
        this.selectedId.set(null);
        this.toasts.show('info', `Deleted ${list.name}.`);
        this.load();
      },
      error: (err: unknown) => this.toasts.show('bad', errorMessage(err)),
    });
  }

  protected open(symbol: string): void {
    void this.router.navigate(['/trade', symbol]);
  }
}
