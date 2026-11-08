# ADR 0002: Product matching by GTIN, curated SKU mapping and normalised pack sizes

- Status: accepted
- Date: 2026-11-08

## Context

"The same basket in every market" needs three things:

1. **Identity**: knowing that a line in market A's feed and one in market B's feed are the same product.
2. **Comparable quantities**: "1 L", "1lt", "1000 ml" and "1,0 LT" are the same size, and a 500 ml pack is half a
   1 L pack.
3. **Substitutes**: when a market does not sell the exact product, what counts as an acceptable stand-in, and how
   many packs of it.

The four simulated markets reproduce the real-world differences: barcodes as EAN-13 or GTIN-14, one market with no
barcodes at all (internal SKUs), lira decimals vs kuruş integers, decimal comma vs point, and size text in Turkish
abbreviations. The feed formats are **synthetic**. No real retailer format is copied, and nothing is scraped. They are
modelled on the kinds of variation public retail data shows.

Turkish price-label rules require a unit price next to the sale price on shelves (price per kg, L or piece; see the
*Fiyat Etiketi Yönetmeliği*). Shoppers already compare that way, so the app should too.

## Options considered

### Identity

- **A. Fuzzy name matching** across feeds ("Ovacık Tam Yağlı Süt 1L" vs "OVACIK T.YAĞLI SÜT 1 LT"). Covers everything,
  but it is wrong often enough to make totals untrustworthy, and its errors are silent.
- **B. GTIN as the key, a curated mapping table for SKU-only feeds (chosen).** Exact where barcodes exist. For feeds
  without them, a human-maintained mapping: unmatched items are counted and reported, never guessed.

### Substitution

- **A. Infer substitutes from names or categories.** Would substitute olive oil for sunflower oil.
- **B. Curated substitution groups (chosen)**: `substitution_group` on the product ("UHT whole milk", "red lentils").
  Brands and pack sizes within a group are acceptable stand-ins. This is product data, owned by whoever owns the
  catalogue.

## Decision

**Canonical GTIN-14.** `Gtin.TryNormalize` accepts GTIN-8/12/13/14, strips spaces and dashes, verifies the GS1 mod-10
check digit and left-pads to 14 digits. `raf.product.gtin` is `char(14)` with a format `CHECK`. Property tests show
every single-digit error is rejected. The synthetic catalogue uses the GS1 **restricted-circulation prefix 20**,
which never identifies a real traded product, so no synthetic code can collide with a real barcode.

**SKU-only feeds** go through `raf.market_sku (market_code, sku) → product_id`. Unmapped SKUs are counted in
`feed_run.items_unmatched`.

**Size cross-check.** If a feed states a pack size that parses and disagrees with the catalogue, the item is
**rejected** (`feed size 1 L differs from catalogue 500 ml`). A re-used barcode or a wrong mapping must not silently
turn a 500 ml price into a 1 L price. An integration test covers it.

**Pack sizes** are normalised to a base unit: grams, millilitres or pieces (`UnitSize`). The parser accepts decimal
comma or point, `g/gr/gram/kg/mg`, `ml/cl/l/lt/litre`, `adet/ad/pcs` and multipacks (`6x1,5 L` = 9000 ml). Invalid
inputs such as "1,5 adet" are rejected rather than rounded. The canonical text form parses back to the same value
(property test).

**Unit price** = pack price × (1000 or 1) / base amount, in kuruş, rounded half away from zero as on a shelf label.
It is shown per kg / L / piece on every line.

**Substitution rule** (pure `BasketComparer`, per market and line):

1. The exact product with a usable (not expired) price.
2. Otherwise, if the line allows substitutes: products from the same group **and the same dimension**, with packs =
   ⌈requested amount ÷ pack size⌉. Overbuy may not exceed `MaxOverbuyRatio` (default 50 %). The cheapest *line total*
   wins. Ties are broken by less waste, then by GTIN.
3. Otherwise the line is missing, and the market total is marked incomplete.

The overbuy cap exists because the cheapest unit price is often the biggest pack. Without it, a 5 kg sack would
"substitute" a 500 g bag. In the demo basket, Çarşı lacks the 1 L olive oil. The only candidate is 2 L, which is
100 % extra, so the line is reported missing. A substitution of 15 eggs by 2 × 10 (33 % extra) is accepted.

**Money** is integer kuruş end to end (`bigint` in PostgreSQL, `long` in .NET, `number` with whole values in
TypeScript). Lira decimals are converted once, at parse time.

## Consequences

- Correct by construction where GTINs exist. Coverage of SKU-only markets depends on curation work, and the
  unmatched count makes that work visible.
- Substitution groups are product data that someone has to maintain. Incorrect grouping produces incorrect
  substitutes, but never across dimensions (mass vs volume).
- A substituted line can cost more than the requested item would have cost elsewhere. That is the honest answer to
  "what would I pay here?", and the line says so (`2 × 10 eggs covers 15 eggs × 1`).
- Real retailer formats will differ from these synthetic ones. Each format is a small, golden-tested
  `IFeedFormat`, so adding one does not touch matching or comparison.
