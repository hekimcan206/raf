import type { Comparison, Freshness, FreshnessPolicy, MarketQuote } from './types';

const MINUTE = 60_000;

/**
 * Same thresholds as the server (FreshnessPolicy.cs), evaluated on the device. A comparison saved for offline use
 * keeps ageing: a price that was "fresh" when fetched becomes "aging" or "stale" while the phone is offline, and the
 * UI must say so instead of showing the label frozen at fetch time.
 */
export function classify(observedAt: string | Date, now: Date, policy: FreshnessPolicy): Freshness {
  const ageMinutes = Math.max(0, (now.getTime() - new Date(observedAt).getTime()) / MINUTE);
  if (ageMinutes <= policy.freshForMinutes) return 'fresh';
  if (ageMinutes <= policy.agingForMinutes) return 'aging';
  if (ageMinutes <= policy.expireAfterMinutes) return 'stale';
  return 'expired';
}

/** Short human age: "just now", "12 min", "3 h", "2 d". */
export function ageLabel(since: string | Date | number, now: Date): string {
  const ms = Math.max(0, now.getTime() - new Date(since).getTime());
  const minutes = Math.floor(ms / MINUTE);
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `${hours} h`;
  return `${Math.floor(hours / 24)} d`;
}

/** "12 min ago", or "just now" (never "just now ago"). */
export function ago(since: string | Date | number, now: Date): string {
  const label = ageLabel(since, now);
  return label === 'just now' ? label : `${label} ago`;
}

export interface MarketFreshness {
  freshKurus: number;
  agingKurus: number;
  staleKurus: number;
  /** Share of the priced total resting on stale (or by now expired) prices, 0..1, weighted by money. */
  staleShare: number;
  lines: (Freshness | null)[];
  /** True if a price used in this total has expired since the comparison was computed. */
  hasExpired: boolean;
}

/** Re-evaluates a market's freshness at `now` from the observed_at values, ignoring the labels sent by the server. */
export function marketFreshness(market: MarketQuote, policy: FreshnessPolicy, now: Date): MarketFreshness {
  let fresh = 0;
  let aging = 0;
  let stale = 0;
  let hasExpired = false;
  const lines = market.lines.map((line) => {
    if (!line.observedAt) return null;
    const f = classify(line.observedAt, now, policy);
    if (f === 'fresh') fresh += line.lineTotalKurus;
    else if (f === 'aging') aging += line.lineTotalKurus;
    else {
      stale += line.lineTotalKurus;
      hasExpired ||= f === 'expired';
    }
    return f;
  });
  const total = fresh + aging + stale;
  return { freshKurus: fresh, agingKurus: aging, staleKurus: stale, staleShare: total === 0 ? 0 : stale / total, lines, hasExpired };
}

/** A cached comparison is past its server-declared validity (a price in it has expired): it must be refetched. */
export function isPastValidity(comparison: Comparison, now: Date): boolean {
  return new Date(comparison.validUntil).getTime() <= now.getTime();
}
