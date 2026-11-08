/**
 * Connectivity as the app sees it, as a pure reducer so every transition is unit-tested.
 *
 * Two independent signals: the network (NetInfo / browser online events) and the SignalR connection. The network can
 * be "up" while the API is unreachable, and SignalR can drop while HTTP still works, so they are tracked separately.
 * Resync actions are derived from transitions, not from the current state: coming back is what triggers work.
 */

export type NetworkState = 'online' | 'offline';
export type HubState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

export interface SyncState {
  network: NetworkState;
  hub: HubState;
  /** Server confirmed our basket at least once since the last edit. */
  basketDirty: boolean;
  /** A price in the basket changed (SignalR push) and the comparison has not been refetched yet. */
  comparisonOutdated: boolean;
  lastResyncAt: string | null;
}

export type SyncEvent =
  | { type: 'networkChanged'; online: boolean }
  | { type: 'hubConnecting' }
  | { type: 'hubConnected' }
  | { type: 'hubReconnecting' }
  | { type: 'hubDisconnected' }
  | { type: 'basketEdited' }
  | { type: 'basketPushed' }
  | { type: 'priceChanged' }
  | { type: 'comparisonFetched' }
  | { type: 'resynced'; at: string };

export type ResyncAction = 'pushBasket' | 'rewatchProducts' | 'refetchComparison';

export const initialSyncState: SyncState = {
  network: 'online',
  hub: 'connecting',
  basketDirty: false,
  comparisonOutdated: false,
  lastResyncAt: null,
};

export function reduce(state: SyncState, event: SyncEvent): SyncState {
  switch (event.type) {
    case 'networkChanged':
      return { ...state, network: event.online ? 'online' : 'offline' };
    case 'hubConnecting':
      return { ...state, hub: 'connecting' };
    case 'hubConnected':
      return { ...state, hub: 'connected' };
    case 'hubReconnecting':
      return { ...state, hub: 'reconnecting' };
    case 'hubDisconnected':
      return { ...state, hub: 'disconnected' };
    case 'basketEdited':
      return { ...state, basketDirty: true };
    case 'basketPushed':
      return { ...state, basketDirty: false };
    case 'priceChanged':
      return { ...state, comparisonOutdated: true };
    case 'comparisonFetched':
      return { ...state, comparisonOutdated: false };
    case 'resynced':
      return { ...state, lastResyncAt: event.at };
  }
}

/**
 * What to do after a transition.
 * - Network back: push the basket if edited offline, refetch the comparison (prices may have moved meanwhile).
 * - SignalR (re)connected: the server sees a new connection with no group membership, so re-watch the basket's
 *   products; and changes pushed while disconnected were lost, so refetch the comparison too.
 * - A price push for a basket product: refetch the comparison.
 */
export function resyncPlan(before: SyncState, after: SyncState): ResyncAction[] {
  const actions = new Set<ResyncAction>();
  const networkBack = before.network === 'offline' && after.network === 'online';
  const hubBack = before.hub !== 'connected' && after.hub === 'connected';

  if (networkBack) {
    if (after.basketDirty) actions.add('pushBasket');
    actions.add('refetchComparison');
  }
  if (hubBack) {
    actions.add('rewatchProducts');
    // The very first connect follows a fresh fetch; only a REconnect implies missed pushes.
    if (before.hub === 'reconnecting' || before.hub === 'disconnected') actions.add('refetchComparison');
  }
  if (!before.comparisonOutdated && after.comparisonOutdated && after.network === 'online') {
    actions.add('refetchComparison');
  }
  if (!before.basketDirty && after.basketDirty && after.network === 'online') {
    actions.add('pushBasket');
  }
  return [...actions];
}

export type Banner =
  | { kind: 'offline'; text: string }
  | { kind: 'unreachable'; text: string }
  | { kind: 'reconnecting'; text: string }
  | { kind: 'outdated'; text: string }
  | null;

/** The single most important connectivity message for the top of the screen. */
/** @param resultAge how long ago the saved comparison was fetched ("23 min ago"), or null if there is none. */
export function banner(state: SyncState, resultAge: string | null, apiReachable: boolean): Banner {
  const asOf = resultAge ? ` Showing the comparison saved ${resultAge}.` : '';
  if (state.network === 'offline') return { kind: 'offline', text: `You are offline.${asOf}` };
  if (!apiReachable) return { kind: 'unreachable', text: `Can't reach the price server.${asOf}` };
  if (state.comparisonOutdated) return { kind: 'outdated', text: 'A price in your basket changed. Updating…' };
  if (state.hub === 'reconnecting' || state.hub === 'disconnected') {
    return { kind: 'reconnecting', text: 'Live price updates paused. Reconnecting…' };
  }
  return null;
}
