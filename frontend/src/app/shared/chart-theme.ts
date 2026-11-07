import { ColorType, DeepPartial, ChartOptions, CrosshairMode, LineStyle, TickMarkType, Time } from 'lightweight-charts';
import { MARKET_ZONE } from './format';

/**
 * Axis labels by the scale the chart library picked for each tick. Date ticks are read in UTC because the
 * library chooses them in UTC. In local time, a June 1 bar from an Indian exchange reads as May 31 in the US.
 * Times of day are New York's, so a trading day reads 9:30 AM to 4:00 PM.
 */
export function formatTick(time: Time, type: TickMarkType): string {
  const date = new Date((time as number) * 1000);
  switch (type) {
    case TickMarkType.Year:
      return date.toLocaleDateString(undefined, { year: 'numeric', timeZone: 'UTC' });
    case TickMarkType.Month:
      return date.toLocaleDateString(undefined, { month: 'short', timeZone: 'UTC' });
    case TickMarkType.DayOfMonth:
      return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', timeZone: 'UTC' });
    default:
      return date.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit', timeZone: MARKET_ZONE });
  }
}

/** Chart canvases can't read CSS variables directly, so resolve the current theme's tokens. */
export function cssToken(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

export function chartOptions(): DeepPartial<ChartOptions> {
  const muted = cssToken('--ink-3');
  const rule = cssToken('--rule');
  return {
    autoSize: true,
    layout: {
      background: { type: ColorType.Solid, color: 'transparent' },
      textColor: muted,
      fontFamily: "'IBM Plex Mono', ui-monospace, monospace",
      fontSize: 11,
      attributionLogo: false,
    },
    grid: {
      vertLines: { visible: false },
      horzLines: { color: rule, style: LineStyle.Solid },
    },
    crosshair: {
      mode: CrosshairMode.Normal,
      vertLine: { color: muted, style: LineStyle.Dashed, labelBackgroundColor: cssToken('--ink') },
      horzLine: { color: muted, style: LineStyle.Dashed, labelBackgroundColor: cssToken('--ink') },
    },
    rightPriceScale: { borderColor: rule },
    timeScale: { borderColor: rule, timeVisible: true, secondsVisible: false },
  };
}
