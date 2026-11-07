import { Pipe, PipeTransform, inject } from '@angular/core';
import { MarketInfoStore } from '../core/market-info.store';

/** The market's clock. Times of day on charts, quotes and orders use this, wherever the viewer is. */
export const MARKET_ZONE = 'America/New_York';

/** A moment as a date and time in New York, such as "Nov 2, 9:30 AM ET". */
export function formatMarketTime(date: Date, options: Intl.DateTimeFormatOptions = { month: 'short', day: 'numeric' }): string {
  return `${date.toLocaleString(undefined, { ...options, hour: 'numeric', minute: '2-digit', timeZone: MARKET_ZONE })} ET`;
}

const formatters = new Map<string, Intl.NumberFormat>();

function currencyFormatter(currency: string): Intl.NumberFormat {
  let f = formatters.get(currency);
  if (!f) {
    f = new Intl.NumberFormat(undefined, { style: 'currency', currency });
    formatters.set(currency, f);
  }
  return f;
}

const MINUS = '−';

@Pipe({ name: 'money' })
export class MoneyPipe implements PipeTransform {
  private info = inject(MarketInfoStore);

  transform(value: number | null | undefined, currency?: string): string {
    if (value === null || value === undefined) return '—';
    return currencyFormatter(currency ?? this.info.info().baseCurrency).format(value);
  }
}

/** Always shows a sign, so direction never depends on color alone. */
@Pipe({ name: 'signedMoney' })
export class SignedMoneyPipe implements PipeTransform {
  private info = inject(MarketInfoStore);

  transform(value: number | null | undefined, currency?: string): string {
    if (value === null || value === undefined) return '—';
    const text = currencyFormatter(currency ?? this.info.info().baseCurrency).format(Math.abs(value));
    if (Math.abs(value) < 0.005) return text;
    return (value > 0 ? '+' : MINUS) + text;
  }
}

@Pipe({ name: 'signedPercent' })
export class SignedPercentPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    const text = `${Math.abs(value).toFixed(2)}%`;
    if (Math.abs(value) < 0.005) return text;
    return (value > 0 ? '+' : MINUS) + text;
  }
}

@Pipe({ name: 'qty' })
export class QuantityPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return value.toLocaleString(undefined, { maximumFractionDigits: 4 });
  }
}

@Pipe({ name: 'compact' })
export class CompactNumberPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 }).format(value);
  }
}

/** CSS class for a profit or loss value. */
@Pipe({ name: 'pnlClass' })
export class PnlClassPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    if (value === null || value === undefined || Math.abs(value) < 0.005) return 'flat';
    return value > 0 ? 'gain' : 'loss';
  }
}

@Pipe({ name: 'marketTime' })
export class MarketTimePipe implements PipeTransform {
  transform(value: string): string {
    return formatMarketTime(new Date(value));
  }
}

export const FORMAT_PIPES = [
  MarketTimePipe, MoneyPipe, SignedMoneyPipe, SignedPercentPipe, QuantityPipe, CompactNumberPipe, PnlClassPipe,
] as const;
