import AsyncStorage from '@react-native-async-storage/async-storage';
import { type LocalBasket, newBasket } from '../domain/basket';

const KEY = 'raf-basket';

export function uuid(): string {
  // RFC 4122 v4 from Math.random: the id only has to be unique per device, not unguessable.
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
}

export async function loadBasket(): Promise<LocalBasket> {
  try {
    const raw = await AsyncStorage.getItem(KEY);
    if (raw) return JSON.parse(raw) as LocalBasket;
  } catch {
    // Storage can be unavailable (private browsing); the app still works with an in-memory basket.
  }
  return newBasket(uuid(), new Date());
}

export async function saveBasket(basket: LocalBasket): Promise<void> {
  try {
    await AsyncStorage.setItem(KEY, JSON.stringify(basket));
  } catch {
    // See loadBasket.
  }
}
