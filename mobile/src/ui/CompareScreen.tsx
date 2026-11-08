import { useState } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { ago, marketFreshness } from '../domain/freshness';
import { formatKurus, formatPercent, formatUnitPrice } from '../domain/money';
import type { Comparison, MarketQuote } from '../domain/types';
import { AgeBadge, FreshnessBar, Pill } from './components';
import { colors, space } from './theme';

const STALE_WARNING_SHARE = 0.2;

export function CompareScreen({ comparison, now, loading }: { comparison: Comparison | undefined; now: Date; loading: boolean }) {
  if (!comparison) {
    return (
      <View style={styles.empty}>
        <Text style={styles.emptyText}>{loading ? 'Comparing markets…' : 'Add products to your basket to compare markets.'}</Text>
      </View>
    );
  }

  const cheapest = comparison.markets.find((m) => m.code === comparison.cheapestCompleteMarket);
  return (
    <ScrollView contentContainerStyle={styles.list}>
      {cheapest && (
        <View style={styles.summary} testID="summary">
          <Text style={styles.summaryLabel}>Cheapest complete basket</Text>
          <Text style={styles.summaryTitle}>
            {cheapest.name} · {formatKurus(cheapest.totalKurus)}
          </Text>
          {comparison.savingsVsMostExpensiveKurus !== null && (
            <Text style={styles.summaryNote}>{formatKurus(comparison.savingsVsMostExpensiveKurus)} less than the most expensive complete basket</Text>
          )}
          <Text style={styles.summaryNote}>Prices combined {ago(comparison.computedAt, now)}</Text>
        </View>
      )}
      {comparison.markets.map((m) => (
        <MarketCard key={m.code} market={m} comparison={comparison} now={now} />
      ))}
    </ScrollView>
  );
}

function MarketCard({ market, comparison, now }: { market: MarketQuote; comparison: Comparison; now: Date }) {
  const [open, setOpen] = useState(market.rank === 1);
  const freshness = marketFreshness(market, comparison.policy, now);
  const feedTone = market.feed.status === 'healthy' ? 'ok' : market.feed.status === 'degraded' ? 'warn' : market.feed.status === 'down' ? 'bad' : 'muted';

  return (
    <View style={[styles.card, !market.complete && styles.cardIncomplete]} testID={`market-${market.code}`}>
      <Pressable onPress={() => setOpen(!open)} accessibilityRole="button" accessibilityLabel={`${market.name}, ${open ? 'collapse' : 'expand'}`}>
        <View style={styles.cardHead}>
          <View style={{ flex: 1 }}>
            <Text style={styles.rank}>#{market.rank}</Text>
            <Text style={styles.marketName}>{market.name}</Text>
          </View>
          <View style={{ alignItems: 'flex-end' }}>
            <Text style={[styles.total, !market.complete && styles.totalIncomplete]}>{formatKurus(market.totalKurus)}</Text>
            {!market.complete && <Text style={styles.incompleteNote}>{market.missingLines} item{market.missingLines > 1 ? 's' : ''} missing: not comparable</Text>}
          </View>
        </View>
        <View style={styles.pills}>
          {market.complete ? <Pill text="complete" tone="ok" /> : <Pill text="incomplete" tone="bad" />}
          {market.substitutedLines > 0 && <Pill text={`${market.substitutedLines} substituted`} tone="warn" />}
          <Pill text={`feed ${market.feed.status}`} tone={feedTone} />
        </View>
        <FreshnessBar fresh={freshness.freshKurus} aging={freshness.agingKurus} stale={freshness.staleKurus} />
        {freshness.staleShare >= STALE_WARNING_SHARE && (
          <Text style={styles.staleWarning} testID={`stale-warning-${market.code}`}>
            {formatPercent(freshness.staleShare)} of this total is based on prices older than {Math.round(comparison.policy.agingForMinutes / 60)} h.
          </Text>
        )}
        {freshness.hasExpired && <Text style={styles.staleWarning}>Some prices here have expired since this result was saved. Refresh when online.</Text>}
      </Pressable>

      {open &&
        market.lines.map((line, i) => {
          const f = freshness.lines[i];
          return (
            <View key={line.requestedProductId} style={styles.line}>
              <View style={{ flex: 1, gap: 2 }}>
                <Text style={[styles.lineName, line.status === 'missing' && styles.missing]}>
                  {line.quantity} × {line.requestedName}
                </Text>
                {line.status === 'substituted' && (
                  <Text style={styles.sub}>
                    ↳ {line.packs} × {line.usedName}
                  </Text>
                )}
                {line.status === 'missing' && <Text style={styles.sub}>{line.note}</Text>}
                {line.observedAt && f && <AgeBadge observedAt={line.observedAt} freshness={f} now={now} />}
              </View>
              {line.status !== 'missing' && (
                <View style={{ alignItems: 'flex-end' }}>
                  <Text style={styles.lineTotal}>{formatKurus(line.lineTotalKurus)}</Text>
                  <Text style={styles.unit}>{formatUnitPrice(line.unitPriceKurus, line.unitPriceBasis)}</Text>
                </View>
              )}
            </View>
          );
        })}
    </View>
  );
}

const styles = StyleSheet.create({
  list: { padding: space(4), gap: space(3), paddingBottom: space(16) },
  empty: { flex: 1, alignItems: 'center', justifyContent: 'center', padding: space(8) },
  emptyText: { color: colors.muted, fontSize: 15, textAlign: 'center' },
  summary: { backgroundColor: colors.primary, borderRadius: 14, padding: space(4), gap: 4 },
  summaryLabel: { color: '#CFE9E2', fontSize: 12, fontWeight: '600', textTransform: 'uppercase', letterSpacing: 0.5 },
  summaryTitle: { color: '#FFFFFF', fontSize: 20, fontWeight: '700' },
  summaryNote: { color: '#E3F2EE', fontSize: 13 },
  card: { backgroundColor: colors.card, borderRadius: 14, padding: space(4), gap: space(2.5), borderWidth: 1, borderColor: colors.border },
  cardIncomplete: { opacity: 0.92, borderStyle: 'dashed' },
  cardHead: { flexDirection: 'row', alignItems: 'flex-start' },
  rank: { fontSize: 12, color: colors.muted, fontWeight: '700' },
  marketName: { fontSize: 17, fontWeight: '700', color: colors.text },
  total: { fontSize: 19, fontWeight: '800', color: colors.text, fontVariant: ['tabular-nums'] },
  totalIncomplete: { color: colors.muted, textDecorationLine: 'line-through' },
  incompleteNote: { fontSize: 11, color: colors.dangerText, marginTop: 2 },
  pills: { flexDirection: 'row', flexWrap: 'wrap', gap: space(1.5), marginVertical: space(1) },
  staleWarning: { fontSize: 13, color: colors.dangerText, backgroundColor: colors.dangerBg, padding: space(2), borderRadius: 8, marginTop: space(1.5) },
  line: { flexDirection: 'row', gap: space(3), paddingTop: space(2.5), borderTopWidth: 1, borderTopColor: colors.border },
  lineName: { fontSize: 14, color: colors.text, fontWeight: '500' },
  missing: { color: colors.missing, textDecorationLine: 'line-through' },
  sub: { fontSize: 12, color: colors.muted },
  lineTotal: { fontSize: 14, fontWeight: '700', color: colors.text, fontVariant: ['tabular-nums'] },
  unit: { fontSize: 11, color: colors.muted },
});
