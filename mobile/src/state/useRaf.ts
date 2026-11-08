import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useReducer, useRef, useState } from 'react';
import { SAMPLE_BASKET_ID, api, isUnreachable } from '../api/client';
import { type LocalBasket, basketKey, markSynced, newBasket, replaceItems, setQuantity } from '../domain/basket';
import { ago, isPastValidity } from '../domain/freshness';
import type { FeedStatus } from '../domain/types';
import { PriceHub } from '../sync/priceHub';
import { type ResyncAction, banner, initialSyncState, reduce, resyncPlan } from '../sync/syncState';
import { loadBasket, saveBasket, uuid } from './basketStorage';
import { wireOnlineManager } from './queryClient';

/**
 * The app's controller: local basket, server queries, the SignalR connection and the resync rules, glued
 * together. The decisions live in pure modules (syncState, freshness, basket) that are unit-tested; this hook only
 * wires events to them and runs the actions they return.
 */
export function useRaf() {
  const queryClient = useQueryClient();
  const [basket, setBasket] = useState<LocalBasket>(() => newBasket(uuid(), new Date()));
  const [loaded, setLoaded] = useState(false);
  const [sync, dispatch] = useReducer(reduce, initialSyncState);
  const [now, setNow] = useState(() => new Date());
  const [lastChange, setLastChange] = useState<string | null>(null);
  const [feedStatus, setFeedStatus] = useState<Record<string, FeedStatus>>({});
  const hub = useRef<PriceHub | null>(null);
  const basketRef = useRef(basket);
  basketRef.current = basket;

  // Re-render every 30 s so "as of" ages and freshness colours move while the screen is open (and offline).
  useEffect(() => {
    const t = setInterval(() => setNow(new Date()), 30_000);
    return () => clearInterval(t);
  }, []);

  useEffect(() => {
    void loadBasket().then((b) => {
      setBasket(b);
      setLoaded(true);
      if (b.dirty) dispatch({ type: 'basketEdited' });
    });
  }, []);

  useEffect(() => {
    if (loaded) void saveBasket(basket);
  }, [basket, loaded]);

  useEffect(() => wireOnlineManager((online) => dispatch({ type: 'networkChanged', online })), []);

  const products = useQuery({ queryKey: ['products'], queryFn: api.products, staleTime: 60 * 60 * 1000 });

  const key = basketKey(basket.items);
  const comparison = useQuery({
    queryKey: ['comparison', key],
    queryFn: async () => {
      const result = await api.compare(basketRef.current.items);
      dispatch({ type: 'comparisonFetched' });
      return result;
    },
    enabled: loaded && basket.items.length > 0,
    // Keep showing the previous basket's result while the new one loads, instead of flashing an empty screen.
    placeholderData: (previous) => previous,
  });

  // A saved result whose prices have expired is not shown as if valid: force a refetch attempt.
  useEffect(() => {
    if (comparison.data && isPastValidity(comparison.data, now)) void comparison.refetch();
  }, [comparison, now]);

  const pushBasket = useCallback(async () => {
    const snapshot = basketRef.current;
    try {
      await api.saveBasket(snapshot.id, snapshot.name, snapshot.items);
      setBasket((current) => markSynced(current, snapshot.updatedAt));
      dispatch({ type: 'basketPushed' });
    } catch {
      // Stays dirty; the next "network back" transition retries.
    }
  }, []);

  const run = useCallback(
    (actions: ResyncAction[]) => {
      for (const action of actions) {
        if (action === 'pushBasket') void pushBasket();
        if (action === 'rewatchProducts') void hub.current?.watch(basketRef.current.items.map((i) => i.productId));
        if (action === 'refetchComparison') void queryClient.invalidateQueries({ queryKey: ['comparison'] });
      }
      if (actions.length > 0) dispatch({ type: 'resynced', at: new Date().toISOString() });
    },
    [pushBasket, queryClient],
  );

  const previous = useRef(sync);
  useEffect(() => {
    const plan = resyncPlan(previous.current, sync);
    previous.current = sync;
    run(plan);
  }, [sync, run]);

  // SignalR lifetime: one connection for the app, watching whatever is in the basket.
  useEffect(() => {
    const h = new PriceHub({
      onPriceChanged: (e) => {
        if (basketRef.current.items.length === 0) return;
        setLastChange(`${e.marketCode} · ${new Date(e.observedAt).toLocaleTimeString()}`);
        dispatch({ type: 'priceChanged' });
      },
      onFeedHealthChanged: (e) => setFeedStatus((s) => ({ ...s, [e.marketCode]: e.status })),
      onState: (state) =>
        dispatch({
          type: state === 'connected' ? 'hubConnected' : state === 'reconnecting' ? 'hubReconnecting' : state === 'connecting' ? 'hubConnecting' : 'hubDisconnected',
        }),
    });
    hub.current = h;
    void h.start();
    return () => {
      void h.stop();
    };
  }, []);

  useEffect(() => {
    void hub.current?.watch(basket.items.map((i) => i.productId));
  }, [key, basket.items]);

  const changeQuantity = useCallback((productId: string, quantity: number) => {
    setBasket((b) => {
      const next = setQuantity(b, productId, quantity, new Date());
      if (next !== b) dispatch({ type: 'basketEdited' });
      return next;
    });
  }, []);

  const loadSample = useCallback(async () => {
    try {
      const sample = await api.basket(SAMPLE_BASKET_ID);
      setBasket((b) => replaceItems({ ...b, name: sample.name }, sample.items, new Date()));
      dispatch({ type: 'basketEdited' });
    } catch {
      // Offline on first launch: nothing to load; the empty-state text stays.
    }
  }, []);

  const unreachable = isUnreachable(comparison.error);
  const resultAge = comparison.data ? ago(comparison.dataUpdatedAt, now) : null;

  return {
    basket,
    products: products.data,
    productsError: products.isError,
    comparison: comparison.data,
    comparisonLoading: comparison.isFetching,
    now,
    sync,
    feedStatus,
    lastChange,
    banner: banner(sync, resultAge, !unreachable),
    changeQuantity,
    loadSample,
  };
}
