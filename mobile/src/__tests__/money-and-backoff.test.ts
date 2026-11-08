import { formatKurus, formatPercent, formatUnitPrice } from '../domain/money';
import { foreverRetryPolicy, reconnectDelayMs } from '../sync/backoff';

describe('formatKurus', () => {
  it.each([
    [0, '0,00 ₺'],
    [5, '0,05 ₺'],
    [3995, '39,95 ₺'],
    [124990, '1.249,90 ₺'],
    [123456789, '1.234.567,89 ₺'],
    [-1250, '−12,50 ₺'],
  ])('%i kuruş → %s', (k, s) => {
    expect(formatKurus(k as number)).toBe(s);
  });

  it('formats unit prices per kg, L or piece', () => {
    expect(formatUnitPrice(8373, 'kg')).toBe('83,73 ₺/kg');
    expect(formatUnitPrice(700, 'piece')).toBe('7,00 ₺/pc');
    expect(formatUnitPrice(null, null)).toBeNull();
  });

  it('formats shares as whole percentages', () => {
    expect(formatPercent(0.375)).toBe('38%');
  });
});

describe('reconnect backoff', () => {
  it('reconnects immediately the first time', () => {
    expect(reconnectDelayMs(0)).toBe(0);
  });

  it('grows exponentially and caps at 30 s', () => {
    const max = () => 1; // jitter at its upper bound
    expect([1, 2, 3, 4, 5, 6, 10].map((a) => reconnectDelayMs(a, max))).toEqual([1000, 2000, 4000, 8000, 16000, 30000, 30000]);
  });

  it('jitters between 50 % and 100 % of the base delay', () => {
    expect(reconnectDelayMs(3, () => 0)).toBe(2000);
    expect(reconnectDelayMs(3, () => 0.999)).toBeLessThanOrEqual(4000);
  });

  it('never gives up, unlike the default SignalR policy', () => {
    for (const previousRetryCount of [0, 4, 10, 1000]) {
      expect(foreverRetryPolicy.nextRetryDelayInMilliseconds({ previousRetryCount })).not.toBeNull();
    }
  });
});
