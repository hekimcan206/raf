import type { BasketItem, Comparison, Product } from '../domain/types';

/**
 * Same origin by default: the compose demo serves the web build and proxies /api and /hubs to the API.
 * For a device or the Expo dev server, set EXPO_PUBLIC_API_URL (e.g. http://192.168.1.20:58280).
 */
export const API_URL = (process.env.EXPO_PUBLIC_API_URL ?? '').replace(/\/$/, '');

const TIMEOUT_MS = 8_000;

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number | null,
  ) {
    super(message);
  }
}

/**
 * Network failure or timeout, as opposed to the server answering with an error. A structural check rather than
 * instanceof: subclassing Error is not reliable across every JS engine and transpiler setup.
 */
export function isUnreachable(error: unknown): boolean {
  return typeof error === 'object' && error !== null && 'status' in error && (error as ApiError).status === null;
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), TIMEOUT_MS);
  let response: Response;
  try {
    response = await fetch(`${API_URL}${path}`, {
      ...init,
      signal: controller.signal,
      headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...init?.headers },
    });
  } catch (e) {
    throw new ApiError(e instanceof Error ? e.message : 'network error', null);
  } finally {
    clearTimeout(timer);
  }
  if (!response.ok) {
    throw new ApiError(`HTTP ${response.status}`, response.status);
  }
  return (await response.json()) as T;
}

export const api = {
  products: () => request<Product[]>('/api/v1/products?limit=200'),
  compare: (items: BasketItem[]) => request<Comparison>('/api/v1/comparisons', { method: 'POST', body: JSON.stringify({ items }) }),
  saveBasket: (id: string, name: string, items: BasketItem[]) =>
    request<{ id: string; version: number }>(`/api/v1/baskets/${id}`, { method: 'PUT', body: JSON.stringify({ name, items }) }),
  basket: (id: string) => request<{ id: string; name: string; items: BasketItem[] }>(`/api/v1/baskets/${id}`),
};

/** The demo basket seeded by the API (docs/README): lets a fresh install show something meaningful. */
export const SAMPLE_BASKET_ID = '5eed0000-0000-4000-8000-00000000ba5e';
