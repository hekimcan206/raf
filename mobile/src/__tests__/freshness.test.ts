import { ageLabel, ago, classify, isPastValidity, marketFreshness } from '../domain/freshness';
import type { Comparison, FreshnessPolicy, MarketQuote, QuoteLine } from '../domain/types';

const policy: FreshnessPolicy = { freshForMinutes: 360, agingForMinutes: 1440, expireAfterMinutes: 10080 };
const now = new Date('2026-11-07T12:00:00Z');
const minutesAgo = (m: number) => new Date(now.getTime() - m * 60_000).toISOString();

function line(total: number, observedAt: string | null, freshness: QuoteLine['freshness'] = 'fresh'): QuoteLine {
  return {
    requestedProductId: Math.random().toString(),
    requestedName: 'p',
    quantity: 1,
    status: observedAt ? 'exact' : 'missing',
    usedProductId: null,
    usedName: null,
    usedSize: null,
    packs: observedAt ? 1 : 0,
    packPriceKurus: total,
    lineTotalKurus: total,
    unitPriceKurus: total,
    unitPriceBasis: 'kg',
    observedAt,
    freshness,
    note: null,
  };
}

function market(lines: QuoteLine[]): MarketQuote {
  return {
    code: 'm',
    name: 'M',
    rank: 1,
    totalKurus: lines.reduce((s, l) => s + l.lineTotalKurus, 0),
    complete: true,
    missingLines: 0,
    substitutedLines: 0,
    freshness: { freshKurus: 0, agingKurus: 0, staleKurus: 0, staleShare: 0, oldestObservedAt: null },
    feed: { status: 'healthy', lastSuccessAt: null },
    lines,
  };
}

describe('classify', () => {
  it.each([
    [0, 'fresh'],
    [360, 'fresh'],
    [361, 'aging'],
    [1440, 'aging'],
    [1441, 'stale'],
    [10080, 'stale'],
    [10081, 'expired'],
  ])('%i minutes old is %s (same thresholds as the server)', (age, expected) => {
    expect(classify(minutesAgo(age as number), now, policy)).toBe(expected);
  });

  it('treats a timestamp slightly in the future as fresh', () => {
    expect(classify(new Date(now.getTime() + 60_000), now, policy)).toBe('fresh');
  });
});

describe('ageLabel', () => {
  it.each([
    [0, 'just now'],
    [12, '12 min'],
    [59, '59 min'],
    [60, '1 h'],
    [47 * 60, '47 h'],
    [49 * 60, '2 d'],
  ])('%i minutes → %s', (m, label) => {
    expect(ageLabel(minutesAgo(m as number), now)).toBe(label);
  });
});

describe('ago', () => {
  it('never says "just now ago"', () => {
    expect(ago(now, now)).toBe('just now');
    expect(ago(minutesAgo(5), now)).toBe('5 min ago');
  });
});

describe('marketFreshness', () => {
  it('re-evaluates labels at read time, ignoring the labels frozen in the cached response', () => {
    // Fetched when the price was 5 h old ("fresh"); the phone stayed offline for 2 days.
    const cached = market([line(1000, minutesAgo(5 * 60), 'fresh')]);
    expect(marketFreshness(cached, policy, now).lines).toEqual(['fresh']);
    const later = new Date(now.getTime() + 2 * 24 * 60 * 60_000);
    const aged = marketFreshness(cached, policy, later);
    expect(aged.lines).toEqual(['stale']);
    expect(aged.staleShare).toBe(1);
  });

  it('weights the stale share by money, not by line count', () => {
    const m = market([line(1000, minutesAgo(10)), line(1000, minutesAgo(10)), line(8000, minutesAgo(3 * 24 * 60))]);
    const f = marketFreshness(m, policy, now);
    expect(f.staleShare).toBeCloseTo(0.8);
    expect(f.freshKurus).toBe(2000);
    expect(f.staleKurus).toBe(8000);
  });

  it('flags prices that expired after the result was saved', () => {
    const m = market([line(1000, minutesAgo(10081))]);
    expect(marketFreshness(m, policy, now).hasExpired).toBe(true);
  });

  it('ignores missing lines', () => {
    const f = marketFreshness(market([line(0, null, null)]), policy, now);
    expect(f.lines).toEqual([null]);
    expect(f.staleShare).toBe(0);
  });
});

describe('isPastValidity', () => {
  it('is true once the first used price has expired', () => {
    const c = { validUntil: '2026-11-07T11:59:59Z' } as Comparison;
    expect(isPastValidity(c, now)).toBe(true);
    expect(isPastValidity({ validUntil: '2026-11-08T00:00:00Z' } as Comparison, now)).toBe(false);
  });
});
