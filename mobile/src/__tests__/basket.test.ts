import { MAX_QUANTITY, basketKey, markSynced, newBasket, quantityOf, replaceItems, setQuantity } from '../domain/basket';

const t0 = new Date('2026-11-07T12:00:00Z');
const t1 = new Date('2026-11-07T12:01:00Z');

describe('local basket', () => {
  it('adds, changes and removes items and marks the basket dirty', () => {
    let b = newBasket('b1', t0);
    expect(b.dirty).toBe(false);
    b = setQuantity(b, 'p1', 2, t1);
    expect(quantityOf(b, 'p1')).toBe(2);
    expect(b.dirty).toBe(true);
    expect(b.updatedAt).toBe(t1.toISOString());
    b = setQuantity(b, 'p1', 0, t1);
    expect(b.items).toEqual([]);
  });

  it('clamps quantities to the server limits', () => {
    const b = setQuantity(newBasket('b', t0), 'p', 500, t1);
    expect(quantityOf(b, 'p')).toBe(MAX_QUANTITY);
  });

  it('returns the same object when nothing changes, so no needless push is triggered', () => {
    const b = setQuantity(newBasket('b', t0), 'p', 1, t1);
    expect(setQuantity(b, 'p', 1, t1)).toBe(b);
    expect(setQuantity(b, 'missing', 0, t1)).toBe(b);
  });

  it('clears dirty only if nothing changed during the push', () => {
    const b = setQuantity(newBasket('b', t0), 'p', 1, t0);
    expect(markSynced(b, b.updatedAt).dirty).toBe(false);
    const editedMeanwhile = setQuantity(b, 'p', 2, t1);
    expect(markSynced(editedMeanwhile, b.updatedAt).dirty).toBe(true);
  });

  it('replaces items from the sample basket', () => {
    const b = replaceItems(newBasket('b', t0), [{ productId: 'x', quantity: 2, allowSubstitutes: true }], t1);
    expect(b.items).toHaveLength(1);
    expect(b.dirty).toBe(true);
  });

  it('builds an order-insensitive cache key', () => {
    const a = [
      { productId: 'b', quantity: 1, allowSubstitutes: true },
      { productId: 'a', quantity: 2, allowSubstitutes: false },
    ];
    expect(basketKey(a)).toBe(basketKey([...a].reverse()));
    expect(basketKey(a)).toBe('a:2:0;b:1:1');
  });
});
