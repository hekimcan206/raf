import AsyncStorage from '@react-native-async-storage/async-storage';
import NetInfo from '@react-native-community/netinfo';
import { createAsyncStoragePersister } from '@tanstack/query-async-storage-persister';
import { QueryClient, onlineManager } from '@tanstack/react-query';
import { Platform } from 'react-native';

const DAY = 24 * 60 * 60 * 1000;

/**
 * Offline-first query client:
 * - networkMode 'offlineFirst': a query runs once even when offline (the request fails fast and the cached data
 *   stays on screen) instead of being paused forever;
 * - gcTime 7 days: a saved comparison survives app restarts for as long as its prices can still be used
 *   (the server's expiry window); after that it is useless anyway;
 * - the whole cache is persisted to AsyncStorage (localStorage on the web).
 */
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      networkMode: 'offlineFirst',
      gcTime: 7 * DAY,
      staleTime: 30_000,
      retry: 1,
      refetchOnWindowFocus: true,
      refetchOnReconnect: true,
    },
    mutations: { networkMode: 'offlineFirst' },
  },
});

export const persister = createAsyncStoragePersister({ storage: AsyncStorage, key: 'raf-query-cache', throttleTime: 1_000 });

export const PERSIST_MAX_AGE = 7 * DAY;

/** Bump when the cached shape changes, so an old cache is discarded instead of misread. */
export const CACHE_BUSTER = 'v1';

/**
 * React Native has no navigator.onLine: drive TanStack Query's online state from NetInfo. On the web, NetInfo only
 * listens to navigator.connection "change" when that API exists (Chromium), which does not fire for every offline
 * transition, so the browser's own online/offline events are wired as well.
 */
export function wireOnlineManager(onChange: (online: boolean) => void): () => void {
  let unsubscribe = () => {};
  onlineManager.setEventListener((setOnline) => {
    const update = (online: boolean) => {
      setOnline(online);
      onChange(online);
    };
    const stopNetInfo = NetInfo.addEventListener((state) => update(state.isConnected !== false));
    const onOnline = () => update(true);
    const onOffline = () => update(false);
    if (Platform.OS === 'web' && typeof window !== 'undefined') {
      window.addEventListener('online', onOnline);
      window.addEventListener('offline', onOffline);
    }
    unsubscribe = () => {
      stopNetInfo();
      if (Platform.OS === 'web' && typeof window !== 'undefined') {
        window.removeEventListener('online', onOnline);
        window.removeEventListener('offline', onOffline);
      }
    };
    return unsubscribe;
  });
  return () => unsubscribe();
}
