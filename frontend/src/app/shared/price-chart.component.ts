import { Component, ElementRef, OnDestroy, afterNextRender, effect, inject, input, signal, viewChild } from '@angular/core';
import {
  CandlestickData, CandlestickSeries, IChartApi, IPriceLine, ISeriesApi, LineStyle, MouseEventParams, Time, UTCTimestamp,
  createChart,
} from 'lightweight-charts';
import { ThemeService } from '../core/theme.service';
import { Candle, ChartRange, Quote } from '../core/models';
import { chartOptions, cssToken, formatTick } from './chart-theme';
import { formatMarketTime } from './format';

export interface ChartLine {
  price: number;
  label: string;
  tone: 'gain' | 'loss' | 'neutral';
}

const BAR_SECONDS: Record<ChartRange, number> = {
  '1D': 300, '5D': 900, '1M': 3600, '6M': 86400, '1Y': 86400, '5Y': 604800,
};

/** Candlesticks: rising bars are hollow and falling bars solid, so direction doesn't rely on color. */
@Component({
  selector: 'app-price-chart',
  template: `
    <div class="chart-readout" aria-live="off">
      @if (readout(); as r) {
        <span>{{ r.when }}</span>
        <span>O <b>{{ r.open }}</b></span>
        <span>H <b>{{ r.high }}</b></span>
        <span>L <b>{{ r.low }}</b></span>
        <span>C <b>{{ r.close }}</b></span>
      } @else {
        <span class="muted">Hover or tap the chart for open, high, low and close</span>
      }
    </div>
    <div #host class="chart-host" role="img" [attr.aria-label]="ariaLabel()"></div>
  `,
})
export class PriceChartComponent implements OnDestroy {
  readonly candles = input.required<Candle[]>();
  readonly range = input.required<ChartRange>();
  readonly quote = input<Quote | null>(null);
  readonly lines = input<ChartLine[]>([]);
  readonly ariaLabel = input('Price chart');

  private host = viewChild.required<ElementRef<HTMLElement>>('host');
  private theme = inject(ThemeService);
  private ready = signal(false);
  private chart?: IChartApi;
  private series?: ISeriesApi<'Candlestick'>;
  private priceLines: IPriceLine[] = [];
  private last?: CandlestickData<UTCTimestamp>;

  protected readonly readout = signal<{ when: string; open: string; high: string; low: string; close: string } | null>(null);

  constructor() {
    afterNextRender(() => {
      this.chart = createChart(this.host().nativeElement, chartOptions());
      this.series = this.chart.addSeries(CandlestickSeries);
      this.chart.subscribeCrosshairMove((p) => this.onCrosshair(p));
      this.ready.set(true);
    });

    effect(() => {
      this.theme.theme();
      if (!this.ready()) return;
      const base = chartOptions();
      this.chart!.applyOptions({
        ...base,
        localization: { timeFormatter: (t: Time) => this.formatTime(t as number) },
        timeScale: { ...base.timeScale, tickMarkFormatter: formatTick },
      });
      const up = cssToken('--gain');
      const down = cssToken('--loss');
      this.series!.applyOptions({
        upColor: 'transparent', borderUpColor: up, wickUpColor: up,
        downColor: down, borderDownColor: down, wickDownColor: down,
      });
      this.drawLines(this.lines());
    });

    effect(() => {
      const candles = this.candles();
      this.range();
      if (!this.ready()) return;
      const data = toSeries(candles);
      this.series!.setData(data);
      this.last = data.at(-1);
      this.chart!.timeScale().fitContent();
    });

    effect(() => {
      const quote = this.quote();
      if (this.ready() && quote) this.applyTick(quote);
    });

    effect(() => {
      const lines = this.lines();
      if (this.ready()) this.drawLines(lines);
    });
  }

  ngOnDestroy(): void {
    this.chart?.remove();
  }

  private applyTick(quote: Quote): void {
    if (!this.last || !this.series) return;

    const barSeconds = BAR_SECONDS[this.range()];
    // Step along the series' own grid: hourly bars from a 9:30 open sit on the half hour, not the hour.
    const elapsedBars = Math.floor((Date.now() / 1000 - this.last.time) / barSeconds);
    const startsNewBar = quote.isMarketOpen && barSeconds < 86400 && elapsedBars >= 1;
    const nowBar = this.last.time + elapsedBars * barSeconds;

    const bar: CandlestickData<UTCTimestamp> = startsNewBar
      ? { time: nowBar as UTCTimestamp, open: this.last.close, high: Math.max(this.last.close, quote.price), low: Math.min(this.last.close, quote.price), close: quote.price }
      : { ...this.last, high: Math.max(this.last.high, quote.price), low: Math.min(this.last.low, quote.price), close: quote.price };

    this.series.update(bar);
    this.last = bar;
  }

  private drawLines(lines: ChartLine[]): void {
    if (!this.series) return;
    this.priceLines.forEach((l) => this.series!.removePriceLine(l));
    const color = { gain: cssToken('--gain'), loss: cssToken('--loss'), neutral: cssToken('--ink-2') };
    this.priceLines = lines.map((l) =>
      this.series!.createPriceLine({
        price: l.price,
        title: l.label,
        color: color[l.tone],
        lineWidth: 1,
        lineStyle: LineStyle.Dashed,
        axisLabelVisible: true,
      }),
    );
  }

  private onCrosshair(p: MouseEventParams<Time>): void {
    const bar = p.time && this.series ? (p.seriesData.get(this.series) as CandlestickData<Time> | undefined) : undefined;
    if (!bar) {
      this.readout.set(null);
      return;
    }
    this.readout.set({
      when: this.formatTime(bar.time as number),
      open: bar.open.toFixed(2),
      high: bar.high.toFixed(2),
      low: bar.low.toFixed(2),
      close: bar.close.toFixed(2),
    });
  }

  private formatTime(seconds: number): string {
    const date = new Date(seconds * 1000);
    return BAR_SECONDS[this.range()] < 86400
      ? formatMarketTime(date)
      : date.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric', timeZone: 'UTC' });
  }
}

/** The chart library throws on out-of-order or repeated times, and Yahoo's newest bar can repeat a timestamp. */
function toSeries(candles: Candle[]): CandlestickData<UTCTimestamp>[] {
  const byTime = new Map<number, Candle>();
  for (const c of candles) byTime.set(c.time, c);
  return [...byTime.values()]
    .sort((a, b) => a.time - b.time)
    .map((c) => ({ time: c.time as UTCTimestamp, open: c.open, high: c.high, low: c.low, close: c.close }));
}
