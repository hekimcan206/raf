import type { BasketItem } from './types';

/**
 * The basket lives on the device first. Edits work offline; `dirty` means the server copy is behind and must be
 * pushed on the next resync. The id is created on the device so an offline-created basket keeps it on the server.
 */
export interface LocalBasket {
  id: string;
  name: string;
  items: BasketItem[];
  updatedAt: string;
  dirty: boolean;
}

export const MAX_QUANTITY = 99;

export function newBasket(id: string, now: Date, name = 'My basket'): LocalBasket {
  return { id, name, items: [], updatedAt: now.toISOString(), dirty: false };
}

export function setQuantity(basket: LocalBasket, productId: string, quantity: number, now: Date): LocalBasket {
  const q = Math.max(0, Math.min(MAX_QUANTITY, Math.trunc(quantity)));
  const exists = basket.items.some((i) => i.productId === productId);
  const items =
    q === 0
      ? basket.items.filter((i) => i.productId !== productId)
      : exists
        ? basket.items.map((i) => (i.productId === productId ? { ...i, quantity: q } : i))
        : [...basket.items, { productId, quantity: q, allowSubstitutes: true }];
  if (items === basket.items || sameItems(items, basket.items)) return basket;
  return { ...basket, items, updatedAt: now.toISOString(), dirty: true };
}

export function quantityOf(basket: LocalBasket, productId: string): number {
  return basket.items.find((i) => i.productId === productId)?.quantity ?? 0;
}

export function replaceItems(basket: LocalBasket, items: BasketItem[], now: Date): LocalBasket {
  return { ...basket, items: items.map((i) => ({ ...i })), updatedAt: now.toISOString(), dirty: true };
}

export function markSynced(basket: LocalBasket, pushedUpdatedAt: string): LocalBasket {
  // Only clear the flag if nothing changed while the push was in flight.
  return basket.updatedAt === pushedUpdatedAt ? { ...basket, dirty: false } : basket;
}

/** Stable key for the comparison cache: order-insensitive, like the server's cache key. */
export function basketKey(items: BasketItem[]): string {
  return [...items]
    .sort((a, b) => a.productId.localeCompare(b.productId))
    .map((i) => `${i.productId}:${i.quantity}:${i.allowSubstitutes ? 1 : 0}`)
    .join(';');
}

function sameItems(a: BasketItem[], b: BasketItem[]): boolean {
  return a.length === b.length && basketKey(a) === basketKey(b);
}
