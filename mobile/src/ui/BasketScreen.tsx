import { Pressable, SectionList, StyleSheet, Text, View } from 'react-native';
import { type LocalBasket, quantityOf } from '../domain/basket';
import { formatKurus } from '../domain/money';
import type { Product } from '../domain/types';
import { Stepper } from './components';
import { colors, space } from './theme';

const CATEGORY_NAMES: Record<string, string> = {
  dairy: 'Dairy',
  eggs: 'Eggs',
  dry: 'Pantry',
  oil: 'Oils',
  drinks: 'Drinks',
  canned: 'Canned',
  household: 'Household',
};

export function BasketScreen({
  products,
  basket,
  onQuantity,
  onLoadSample,
  productsError,
}: {
  products: Product[] | undefined;
  basket: LocalBasket;
  onQuantity: (productId: string, quantity: number) => void;
  onLoadSample: () => void;
  productsError: boolean;
}) {
  if (!products) {
    return (
      <View style={styles.center}>
        <Text style={styles.muted}>{productsError ? 'The catalogue is not available offline yet. Connect once to load it.' : 'Loading catalogue…'}</Text>
      </View>
    );
  }

  const sections = Object.entries(
    products.reduce<Record<string, Product[]>>((acc, p) => {
      (acc[p.category] ??= []).push(p);
      return acc;
    }, {}),
  ).map(([category, data]) => ({ title: CATEGORY_NAMES[category] ?? category, data }));

  return (
    <SectionList
      sections={sections}
      keyExtractor={(p) => p.id}
      contentContainerStyle={styles.list}
      stickySectionHeadersEnabled={false}
      ListHeaderComponent={
        basket.items.length === 0 ? (
          <View style={styles.hint}>
            <Text style={styles.hintText}>Your basket is empty. Add products below, or start from a typical weekly shop.</Text>
            <Pressable onPress={onLoadSample} style={styles.sampleBtn} accessibilityRole="button" testID="load-sample">
              <Text style={styles.sampleBtnText}>Try the sample basket</Text>
            </Pressable>
          </View>
        ) : undefined
      }
      renderSectionHeader={({ section }) => <Text style={styles.section}>{section.title}</Text>}
      renderItem={({ item }) => (
        <View style={styles.row}>
          <View style={{ flex: 1 }}>
            <Text style={styles.name}>{item.name}</Text>
            <Text style={styles.meta}>
              {item.size} · {item.lowestPriceKurus !== null ? `from ${formatKurus(item.lowestPriceKurus)}` : 'no current price'} · {item.marketsListing}/4 markets
            </Text>
          </View>
          <Stepper value={quantityOf(basket, item.id)} onChange={(q) => onQuantity(item.id, q)} label={item.name} />
        </View>
      )}
    />
  );
}

const styles = StyleSheet.create({
  list: { padding: space(4), paddingBottom: space(16) },
  center: { flex: 1, alignItems: 'center', justifyContent: 'center', padding: space(8) },
  muted: { color: colors.muted, textAlign: 'center' },
  hint: { backgroundColor: colors.primarySoft, borderRadius: 14, padding: space(4), gap: space(3), marginBottom: space(2) },
  hintText: { color: colors.text, fontSize: 14 },
  sampleBtn: { backgroundColor: colors.primary, borderRadius: 10, paddingVertical: space(2.5), alignItems: 'center' },
  sampleBtnText: { color: '#FFFFFF', fontWeight: '700' },
  section: { fontSize: 13, fontWeight: '700', color: colors.muted, textTransform: 'uppercase', letterSpacing: 0.5, marginTop: space(4), marginBottom: space(2) },
  row: { flexDirection: 'row', alignItems: 'center', backgroundColor: colors.card, padding: space(3), borderRadius: 12, marginBottom: space(2), gap: space(3), borderWidth: 1, borderColor: colors.border },
  name: { fontSize: 15, fontWeight: '600', color: colors.text },
  meta: { fontSize: 12, color: colors.muted, marginTop: 2 },
});
