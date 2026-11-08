import { Pressable, StyleSheet, Text, View } from 'react-native';
import { ageLabel } from '../domain/freshness';
import { formatPercent } from '../domain/money';
import type { Freshness } from '../domain/types';
import type { Banner } from '../sync/syncState';
import { colors, freshnessColor, freshnessLabel, space } from './theme';

export function ConnectivityBanner({ banner }: { banner: Banner }) {
  if (!banner) return null;
  const palette =
    banner.kind === 'offline' || banner.kind === 'unreachable'
      ? { bg: colors.offlineBg, fg: '#FFFFFF' }
      : { bg: colors.warnBg, fg: colors.warnText };
  return (
    <View accessibilityRole="alert" testID={`banner-${banner.kind}`} style={[styles.banner, { backgroundColor: palette.bg }]}>
      <Text style={[styles.bannerText, { color: palette.fg }]}>{banner.text}</Text>
    </View>
  );
}

/** "as of 3 h" badge, coloured by freshness: the age of the market's own price statement. */
export function AgeBadge({ observedAt, freshness, now }: { observedAt: string; freshness: Freshness; now: Date }) {
  return (
    <View style={[styles.badge, { borderColor: freshnessColor[freshness] }]}>
      <View style={[styles.dot, { backgroundColor: freshnessColor[freshness] }]} />
      <Text style={[styles.badgeText, { color: freshnessColor[freshness] }]}>
        {freshnessLabel[freshness]} · {ageLabel(observedAt, now)}
      </Text>
    </View>
  );
}

/** Money-weighted split of a total into fresh / aging / stale. */
export function FreshnessBar({ fresh, aging, stale }: { fresh: number; aging: number; stale: number }) {
  const total = fresh + aging + stale;
  if (total === 0) return null;
  return (
    <View>
      <View style={styles.bar}>
        {fresh > 0 && <View style={{ flex: fresh, backgroundColor: colors.fresh }} />}
        {aging > 0 && <View style={{ flex: aging, backgroundColor: colors.aging }} />}
        {stale > 0 && <View style={{ flex: stale, backgroundColor: colors.stale }} />}
      </View>
      <Text style={styles.barLegend}>
        {formatPercent(fresh / total)} fresh · {formatPercent(aging / total)} aging · {formatPercent(stale / total)} stale
      </Text>
    </View>
  );
}

export function Stepper({ value, onChange, label }: { value: number; onChange: (v: number) => void; label: string }) {
  return (
    <View style={styles.stepper}>
      <Pressable accessibilityLabel={`Remove one ${label}`} onPress={() => onChange(value - 1)} disabled={value === 0} style={[styles.stepBtn, value === 0 && styles.stepBtnDisabled]}>
        <Text style={styles.stepBtnText}>−</Text>
      </Pressable>
      <Text style={styles.stepValue}>{value}</Text>
      <Pressable accessibilityLabel={`Add one ${label}`} onPress={() => onChange(value + 1)} style={styles.stepBtn}>
        <Text style={styles.stepBtnText}>+</Text>
      </Pressable>
    </View>
  );
}

export function Pill({ text, tone }: { text: string; tone: 'ok' | 'warn' | 'bad' | 'muted' }) {
  const map = {
    ok: { bg: colors.primarySoft, fg: colors.primary },
    warn: { bg: colors.warnBg, fg: colors.warnText },
    bad: { bg: colors.dangerBg, fg: colors.dangerText },
    muted: { bg: '#EEF0ED', fg: colors.muted },
  }[tone];
  return (
    <View style={[styles.pill, { backgroundColor: map.bg }]}>
      <Text style={[styles.pillText, { color: map.fg }]}>{text}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  banner: { paddingVertical: space(2.5), paddingHorizontal: space(4) },
  bannerText: { fontSize: 14, fontWeight: '600' },
  badge: { flexDirection: 'row', alignItems: 'center', borderWidth: 1, borderRadius: 999, paddingHorizontal: space(2), paddingVertical: 2, gap: 4, alignSelf: 'flex-start' },
  dot: { width: 6, height: 6, borderRadius: 3 },
  badgeText: { fontSize: 11, fontWeight: '600' },
  bar: { flexDirection: 'row', height: 6, borderRadius: 3, overflow: 'hidden', backgroundColor: colors.border },
  barLegend: { fontSize: 11, color: colors.muted, marginTop: 4 },
  stepper: { flexDirection: 'row', alignItems: 'center', gap: space(2) },
  stepBtn: { width: 32, height: 32, borderRadius: 16, backgroundColor: colors.primarySoft, alignItems: 'center', justifyContent: 'center' },
  stepBtnDisabled: { opacity: 0.4 },
  stepBtnText: { fontSize: 18, color: colors.primary, fontWeight: '700' },
  stepValue: { minWidth: 20, textAlign: 'center', fontSize: 15, fontWeight: '600', color: colors.text },
  pill: { borderRadius: 999, paddingHorizontal: space(2), paddingVertical: 2, alignSelf: 'flex-start' },
  pillText: { fontSize: 11, fontWeight: '700' },
});
