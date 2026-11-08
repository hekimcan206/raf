// Wire types of the raf API (see src/Raf.Api/Comparison/ComparisonService.cs).

export type Freshness = 'fresh' | 'aging' | 'stale' | 'expired';
export type LineStatus = 'exact' | 'substituted' | 'missing';
export type FeedStatus = 'healthy' | 'degraded' | 'down' | 'unknown';

export interface Product {
  id: string;
  gtin: string;
  name: string;
  brand: string;
  category: string;
  size: string;
  unitPriceBasis: string;
  substitutionGroup: string | null;
  lowestPriceKurus: number | null;
  marketsListing: number;
}

export interface FreshnessPolicy {
  freshForMinutes: number;
  agingForMinutes: number;
  expireAfterMinutes: number;
}

export interface QuoteLine {
  requestedProductId: string;
  requestedName: string;
  quantity: number;
  status: LineStatus;
  usedProductId: string | null;
  usedName: string | null;
  usedSize: string | null;
  packs: number;
  packPriceKurus: number;
  lineTotalKurus: number;
  unitPriceKurus: number | null;
  unitPriceBasis: string | null;
  observedAt: string | null;
  freshness: Freshness | null;
  note: string | null;
}

export interface MarketQuote {
  code: string;
  name: string;
  rank: number;
  totalKurus: number;
  complete: boolean;
  missingLines: number;
  substitutedLines: number;
  freshness: {
    freshKurus: number;
    agingKurus: number;
    staleKurus: number;
    staleShare: number;
    oldestObservedAt: string | null;
  };
  feed: { status: FeedStatus; lastSuccessAt: string | null };
  lines: QuoteLine[];
}

export interface Comparison {
  computedAt: string;
  servedAt: string;
  validUntil: string;
  cache: 'hit' | 'miss' | 'bypass';
  policy: FreshnessPolicy;
  cheapestCompleteMarket: string | null;
  savingsVsMostExpensiveKurus: number | null;
  markets: MarketQuote[];
}

export interface BasketItem {
  productId: string;
  quantity: number;
  allowSubstitutes: boolean;
}

export interface PriceChangedEvent {
  productId: string;
  marketCode: string;
  oldPriceKurus: number | null;
  newPriceKurus: number;
  observedAt: string;
}

export interface FeedHealthChangedEvent {
  marketCode: string;
  status: FeedStatus;
  lastSuccessAt: string | null;
}
