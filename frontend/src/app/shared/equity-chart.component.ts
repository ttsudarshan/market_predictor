import { Component, ElementRef, OnDestroy, afterNextRender, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import {
  AreaSeries, IChartApi, IPriceLine, ISeriesApi, LineStyle, MouseEventParams, Time, UTCTimestamp, createChart,
} from 'lightweight-charts';
import { EquityPoint } from '../core/models';
import { MarketInfoStore } from '../core/market-info.store';
import { ThemeService } from '../core/theme.service';
import { chartOptions, cssToken, formatTick } from './chart-theme';
import { formatMarketTime } from './format';

/** Account value over time, with a reference line at the starting balance. */
@Component({
  selector: 'app-equity-chart',
  template: `
    <div class="chart-readout" aria-live="off">
      @if (hover(); as h) {
        <span>{{ h.when }}</span><span><b>{{ h.value }}</b></span>
      } @else {
        <span class="muted">{{ summary() }}</span>
      }
    </div>
    <div #host class="chart-host chart-host-short" role="img" [attr.aria-label]="summary()"></div>
  `,
})
export class EquityChartComponent implements OnDestroy {
  readonly points = input.required<EquityPoint[]>();
  readonly startingBalance = input.required<number>();

  private host = viewChild.required<ElementRef<HTMLElement>>('host');
  private theme = inject(ThemeService);
  private info = inject(MarketInfoStore);
  private ready = signal(false);
  private chart?: IChartApi;
  private series?: ISeriesApi<'Area'>;
  private startLine?: IPriceLine;

  protected readonly hover = signal<{ when: string; value: string } | null>(null);
  protected readonly summary = computed(() => {
    const n = this.points().length;
    return n < 3
      ? 'Your account value is recorded every few minutes, so this fills in as you trade.'
      : `Account value, ${n} data points. Hover for details.`;
  });

  constructor() {
    afterNextRender(() => {
      this.chart = createChart(this.host().nativeElement, chartOptions());
      this.series = this.chart.addSeries(AreaSeries, { lineWidth: 2, priceLineVisible: false });
      this.chart.subscribeCrosshairMove((p) => this.onCrosshair(p));
      this.ready.set(true);
    });

    effect(() => {
      this.theme.theme();
      if (!this.ready()) return;
      const base = chartOptions();
      this.chart!.applyOptions({
        ...base,
        localization: {
          timeFormatter: (t: Time) => this.formatTime(t as number),
          priceFormatter: (v: number) => this.money(v),
        },
        timeScale: {
          ...base.timeScale,
          fixLeftEdge: true,
          fixRightEdge: true,
          tickMarkFormatter: formatTick,
        },
      });
      const accent = cssToken('--accent');
      this.series!.applyOptions({ lineColor: accent, topColor: `${accent}33`, bottomColor: `${accent}00` });
      this.drawStart();
    });

    effect(() => {
      const points = this.points();
      if (!this.ready()) return;
      this.series!.setData(toSeries(points));
      this.chart!.timeScale().fitContent();
    });

    effect(() => {
      this.startingBalance();
      if (this.ready()) this.drawStart();
    });
  }

  ngOnDestroy(): void {
    this.chart?.remove();
  }

  private drawStart(): void {
    if (!this.series) return;
    if (this.startLine) this.series.removePriceLine(this.startLine);
    this.startLine = this.series.createPriceLine({
      price: this.startingBalance(),
      title: 'Start',
      color: cssToken('--ink-3'),
      lineWidth: 1,
      lineStyle: LineStyle.Dotted,
      axisLabelVisible: false,
    });
  }

  private onCrosshair(p: MouseEventParams<Time>): void {
    const point = p.time && this.series ? (p.seriesData.get(this.series) as { value: number } | undefined) : undefined;
    this.hover.set(point ? { when: this.formatTime(p.time as number), value: this.money(point.value) } : null);
  }

  private money(v: number): string {
    return new Intl.NumberFormat(undefined, { style: 'currency', currency: this.info.info().baseCurrency }).format(v);
  }

  private formatTime(seconds: number): string {
    return formatMarketTime(new Date(seconds * 1000));
  }
}

function toSeries(points: EquityPoint[]): { time: UTCTimestamp; value: number }[] {
  const bySecond = new Map<number, number>();
  for (const p of points) bySecond.set(Math.floor(new Date(p.timestamp).getTime() / 1000), p.equity);
  return [...bySecond.entries()]
    .sort((a, b) => a[0] - b[0])
    .map(([time, value]) => ({ time: time as UTCTimestamp, value }));
}
