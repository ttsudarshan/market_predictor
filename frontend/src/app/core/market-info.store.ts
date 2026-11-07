import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiService } from './api.service';
import { MarketInfo, MarketStatus } from './models';

const FALLBACK: MarketInfo = {
  provider: 'Unknown',
  simulated: false,
  baseCurrency: 'USD',
  defaultStartingBalance: 100000,
  commissionPerOrder: 0,
  pollSeconds: 15,
  streaming: false,
};

@Injectable({ providedIn: 'root' })
export class MarketInfoStore {
  private api = inject(ApiService);
  readonly info = signal<MarketInfo>(FALLBACK);

  /** Whether the US market is open, refreshed by the app shell. Null until first loaded or if it can't be read. */
  readonly status = signal<MarketStatus | null>(null);

  /** Never rejects: a failure here shouldn't stop the app from starting and showing its own errors. */
  async load(): Promise<void> {
    try {
      this.info.set(await firstValueFrom(this.api.marketInfo()));
    } catch {
      /* keep the fallback */
    }
  }
}
