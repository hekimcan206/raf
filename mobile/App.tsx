import { PersistQueryClientProvider } from '@tanstack/react-query-persist-client';
import { StatusBar } from 'expo-status-bar';
import { useState } from 'react';
import { Pressable, SafeAreaView, StyleSheet, Text, View } from 'react-native';
import { CACHE_BUSTER, PERSIST_MAX_AGE, persister, queryClient } from './src/state/queryClient';
import { useRaf } from './src/state/useRaf';
import { BasketScreen } from './src/ui/BasketScreen';
import { CompareScreen } from './src/ui/CompareScreen';
import { ConnectivityBanner } from './src/ui/components';
import { colors, space } from './src/ui/theme';

export default function App() {
  return (
    <PersistQueryClientProvider client={queryClient} persistOptions={{ persister, maxAge: PERSIST_MAX_AGE, buster: CACHE_BUSTER }}>
      <Shell />
    </PersistQueryClientProvider>
  );
}

type Tab = 'basket' | 'compare';

function Shell() {
  const raf = useRaf();
  const [tab, setTab] = useState<Tab>('basket');
  const count = raf.basket.items.reduce((n, i) => n + i.quantity, 0);
  const live = raf.sync.network === 'online' && raf.sync.hub === 'connected';

  return (
    <SafeAreaView style={styles.root}>
      <StatusBar style="dark" />
      <View style={styles.header}>
        <Text style={styles.brand}>raf</Text>
        <View style={styles.live} testID="live-indicator">
          <View style={[styles.liveDot, { backgroundColor: live ? colors.fresh : colors.missing }]} />
          <Text style={styles.liveText}>{live ? 'live prices' : raf.sync.network === 'offline' ? 'offline' : 'not live'}</Text>
        </View>
      </View>
      <ConnectivityBanner banner={raf.banner} />
      <View style={styles.tabs}>
        <TabButton label={`Basket${count > 0 ? ` (${count})` : ''}`} active={tab === 'basket'} onPress={() => setTab('basket')} testID="tab-basket" />
        <TabButton label="Compare markets" active={tab === 'compare'} onPress={() => setTab('compare')} testID="tab-compare" />
      </View>
      <View style={styles.body}>
        {tab === 'basket' ? (
          <BasketScreen
            products={raf.products}
            productsError={raf.productsError}
            basket={raf.basket}
            onQuantity={raf.changeQuantity}
            onLoadSample={() => {
              void raf.loadSample().then(() => setTab('compare'));
            }}
          />
        ) : (
          <CompareScreen comparison={raf.comparison} now={raf.now} loading={raf.comparisonLoading} />
        )}
      </View>
    </SafeAreaView>
  );
}

function TabButton({ label, active, onPress, testID }: { label: string; active: boolean; onPress: () => void; testID: string }) {
  return (
    <Pressable onPress={onPress} style={[styles.tab, active && styles.tabActive]} accessibilityRole="tab" accessibilityState={{ selected: active }} testID={testID}>
      <Text style={[styles.tabText, active && styles.tabTextActive]}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: colors.bg },
  header: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', paddingHorizontal: space(4), paddingVertical: space(3) },
  brand: { fontSize: 26, fontWeight: '800', color: colors.primary, letterSpacing: -0.5 },
  live: { flexDirection: 'row', alignItems: 'center', gap: 6 },
  liveDot: { width: 8, height: 8, borderRadius: 4 },
  liveText: { fontSize: 12, color: colors.muted, fontWeight: '600' },
  tabs: { flexDirection: 'row', marginHorizontal: space(4), marginTop: space(2), backgroundColor: '#E9ECE8', borderRadius: 10, padding: 3 },
  tab: { flex: 1, paddingVertical: space(2), alignItems: 'center', borderRadius: 8 },
  tabActive: { backgroundColor: colors.card },
  tabText: { fontSize: 14, fontWeight: '600', color: colors.muted },
  tabTextActive: { color: colors.text },
  body: { flex: 1, width: '100%', maxWidth: 720, alignSelf: 'center' },
});
