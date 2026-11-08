import { type SyncEvent, type SyncState, banner, initialSyncState, reduce, resyncPlan } from '../sync/syncState';

const connected: SyncState = { ...initialSyncState, hub: 'connected' };

function apply(state: SyncState, ...events: SyncEvent[]): SyncState {
  return events.reduce(reduce, state);
}

describe('resyncPlan', () => {
  it('does nothing while nothing changes', () => {
    expect(resyncPlan(connected, connected)).toEqual([]);
  });

  it('refetches the comparison when the network comes back', () => {
    const offline = apply(connected, { type: 'networkChanged', online: false });
    const back = apply(offline, { type: 'networkChanged', online: true });
    expect(resyncPlan(offline, back)).toEqual(['refetchComparison']);
  });

  it('also pushes the basket if it was edited offline', () => {
    const offlineEdited = apply(connected, { type: 'networkChanged', online: false }, { type: 'basketEdited' });
    const back = apply(offlineEdited, { type: 'networkChanged', online: true });
    expect(resyncPlan(offlineEdited, back)).toEqual(expect.arrayContaining(['pushBasket', 'refetchComparison']));
  });

  it('pushes an online edit straight away but not an offline one', () => {
    expect(resyncPlan(connected, apply(connected, { type: 'basketEdited' }))).toEqual(['pushBasket']);
    const offline = apply(connected, { type: 'networkChanged', online: false });
    expect(resyncPlan(offline, apply(offline, { type: 'basketEdited' }))).toEqual([]);
  });

  it('re-watches products and refetches after a SignalR reconnect (groups and missed pushes are lost)', () => {
    const reconnecting = apply(connected, { type: 'hubReconnecting' });
    const back = apply(reconnecting, { type: 'hubConnected' });
    expect(resyncPlan(reconnecting, back)).toEqual(expect.arrayContaining(['rewatchProducts', 'refetchComparison']));
  });

  it('only re-watches on the very first connect (the initial fetch is already fresh)', () => {
    const first = apply(initialSyncState, { type: 'hubConnected' });
    expect(resyncPlan(initialSyncState, first)).toEqual(['rewatchProducts']);
  });

  it('refetches when a price in the basket changes, once', () => {
    const changed = apply(connected, { type: 'priceChanged' });
    expect(resyncPlan(connected, changed)).toEqual(['refetchComparison']);
    expect(resyncPlan(changed, apply(changed, { type: 'priceChanged' }))).toEqual([]);
    expect(apply(changed, { type: 'comparisonFetched' }).comparisonOutdated).toBe(false);
  });

  it('does not try to refetch on a price push while offline', () => {
    const offline = apply(connected, { type: 'networkChanged', online: false });
    expect(resyncPlan(offline, apply(offline, { type: 'priceChanged' }))).toEqual([]);
  });
});

describe('banner', () => {
  it('prefers "offline" and says how old the saved result is', () => {
    const offline = apply(connected, { type: 'networkChanged', online: false });
    expect(banner(offline, '23 min ago', true)).toEqual({ kind: 'offline', text: 'You are offline. Showing the comparison saved 23 min ago.' });
  });

  it('distinguishes "online but the server is unreachable"', () => {
    expect(banner(connected, '2 h ago', false)?.kind).toBe('unreachable');
  });

  it('announces an outdated comparison, then live updates pausing', () => {
    expect(banner(apply(connected, { type: 'priceChanged' }), null, true)?.kind).toBe('outdated');
    expect(banner(apply(connected, { type: 'hubReconnecting' }), null, true)?.kind).toBe('reconnecting');
  });

  it('shows nothing when all is well', () => {
    expect(banner(connected, 'just now', true)).toBeNull();
  });
});
