import type { Freshness } from '../domain/types';

export const colors = {
  bg: '#F6F7F4',
  card: '#FFFFFF',
  text: '#1C2421',
  muted: '#5F6B66',
  border: '#E2E6E1',
  primary: '#0F6E5C',
  primarySoft: '#E3F2EE',
  fresh: '#1E8E5A',
  aging: '#C98A12',
  stale: '#C4472B',
  missing: '#8A8F8C',
  offlineBg: '#2C3330',
  warnBg: '#FFF4E0',
  warnText: '#7A4B00',
  dangerBg: '#FDECE8',
  dangerText: '#8A2A16',
};

export const freshnessColor: Record<Freshness, string> = {
  fresh: colors.fresh,
  aging: colors.aging,
  stale: colors.stale,
  expired: colors.stale,
};

export const freshnessLabel: Record<Freshness, string> = {
  fresh: 'fresh',
  aging: 'aging',
  stale: 'stale',
  expired: 'expired',
};

export const space = (n: number) => n * 4;
