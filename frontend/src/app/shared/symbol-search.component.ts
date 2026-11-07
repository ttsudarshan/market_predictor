import { Component, DestroyRef, ElementRef, inject, input, output, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, catchError, debounceTime, distinctUntilChanged, of, switchMap } from 'rxjs';
import { ApiService } from '../core/api.service';
import { SymbolSearchResult } from '../core/models';

/** Type-ahead symbol finder. Pressing Enter with nothing highlighted uses the typed text as a ticker. */
@Component({
  selector: 'app-symbol-search',
  template: `
    <div class="search" (focusout)="onFocusOut($event)">
      <input
        #box
        type="search"
        role="combobox"
        autocomplete="off"
        spellcheck="false"
        aria-autocomplete="list"
        aria-controls="symbol-results"
        [attr.aria-expanded]="open()"
        [attr.aria-label]="placeholder()"
        [placeholder]="placeholder()"
        (input)="onInput(box.value)"
        (keydown)="onKey($event, box.value)"
      />
      @if (open()) {
        <ul id="symbol-results" class="search-results" role="listbox">
          @for (r of results(); track r.symbol; let i = $index) {
            <li role="option" [attr.aria-selected]="i === active()">
              <button type="button" tabindex="-1" [class.active]="i === active()" (mousedown)="$event.preventDefault()" (click)="pick(r.symbol)">
                <span class="mono strong">{{ r.symbol }}</span>
                <span class="search-name">{{ r.name }}</span>
                <span class="muted small">{{ r.exchange }}</span>
              </button>
            </li>
          } @empty {
            <li class="search-empty muted">{{ failed() ? 'Search is unavailable right now.' : 'No matching stocks or ETFs.' }}</li>
          }
        </ul>
      }
    </div>
  `,
})
export class SymbolSearchComponent {
  readonly placeholder = input('Search stocks and ETFs');
  readonly picked = output<string>();

  private api = inject(ApiService);
  private box = viewChild.required<ElementRef<HTMLInputElement>>('box');
  private queries = new Subject<string>();

  protected readonly results = signal<SymbolSearchResult[]>([]);
  protected readonly active = signal(-1);
  protected readonly open = signal(false);
  protected readonly failed = signal(false);

  constructor() {
    this.queries
      .pipe(
        debounceTime(200),
        distinctUntilChanged(),
        switchMap((q) => {
          if (!q) return of<SymbolSearchResult[] | null>([]);
          return this.api.search(q).pipe(catchError(() => of(null)));
        }),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe((results) => {
        this.failed.set(results === null);
        this.results.set(results ?? []);
        this.active.set(-1);
        this.open.set(this.box().nativeElement.value.trim().length > 0);
      });
  }

  protected onInput(value: string): void {
    this.queries.next(value.trim());
    if (!value.trim()) this.open.set(false);
  }

  protected onKey(event: KeyboardEvent, value: string): void {
    const count = this.results().length;
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        if (count) this.active.update((i) => (i + 1) % count);
        break;
      case 'ArrowUp':
        event.preventDefault();
        if (count) this.active.update((i) => (i <= 0 ? count - 1 : i - 1));
        break;
      case 'Enter': {
        event.preventDefault();
        const highlighted = this.results()[this.active()];
        const symbol = highlighted?.symbol ?? value.trim().toUpperCase();
        if (symbol) this.pick(symbol);
        break;
      }
      case 'Escape':
        this.open.set(false);
        break;
    }
  }

  protected onFocusOut(event: FocusEvent): void {
    const next = event.relatedTarget as Node | null;
    if (!next || !(event.currentTarget as HTMLElement).contains(next)) this.open.set(false);
  }

  protected pick(symbol: string): void {
    this.picked.emit(symbol);
    this.box().nativeElement.value = '';
    this.queries.next('');
    this.results.set([]);
    this.open.set(false);
  }
}
