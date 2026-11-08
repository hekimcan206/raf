/**
 * Turkish lira formatting done by hand so it is identical on Hermes, JSC and the web regardless of ICU data:
 * thousands separated by ".", decimal comma, the ₺ sign after the amount ("1.249,90 ₺").
 */
export function formatKurus(kurus: number): string {
  const negative = kurus < 0;
  const abs = Math.abs(Math.round(kurus));
  const lira = Math.floor(abs / 100);
  const cents = abs % 100;
  const grouped = lira.toString().replace(/\B(?=(\d{3})+(?!\d))/g, '.');
  return `${negative ? '−' : ''}${grouped},${cents.toString().padStart(2, '0')} ₺`;
}

/** "83,73 ₺/kg" */
export function formatUnitPrice(kurus: number | null, basis: string | null): string | null {
  if (kurus === null || basis === null) return null;
  return `${formatKurus(kurus)}/${basis === 'piece' ? 'pc' : basis}`;
}

export function formatPercent(share: number): string {
  return `${Math.round(share * 100)}%`;
}
