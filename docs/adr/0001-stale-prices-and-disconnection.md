# ADR 0001: Stale prices and disconnection are modelled, not hidden

- Status: accepted
- Date: 2026-11-08

## Context

A basket comparison is only as good as the prices behind it, and those prices arrive late, irregularly or not at
all:

- markets publish on their own schedule (a live JSON feed every few minutes, a nightly XML list, a paged API that
  only says when each price last changed);
- feeds go down (maintenance windows, 5xx, hangs);
- some items have not been touched by a market for days;
- the shopper's phone is offline in the supermarket basement, exactly when the comparison matters.

The public Market Fiyatı service run by TÜBİTAK, which collects chain-store prices for Turkish consumers, refreshes
daily ([TÜBİTAK announcement](https://tubitak.gov.tr/tr/haber/zincir-market-fiyatlarina-aninda-erisimin-onu-acildi)).
A daily cadence is normal in this domain. Showing a total as if every price were current would therefore be
misleading, and it is the most common failure of price-comparison apps.

## Decision drivers

1. A shopper must be able to tell how much of a total to trust, at a glance.
2. Being offline must not empty the screen; it must show the last result and say how old it is.
3. Re-reading an old feed, or the device clock moving on while offline, must never make a price look newer.
4. Server and client must use the same rules.

## Options considered

### A. Show only "fresh" prices and drop the rest
Simple, but a market with a down feed or a nightly cadence drops out of the comparison entirely. The shopper loses the
best evidence available, and totals stop being comparable.

### B. Show every price and put a single "last updated" time on the page
Familiar. But one timestamp cannot describe a total built from prices of very different ages. Per market it is
usually the newest feed run, which is the most flattering number available.

### C. Classify each price by the age of its evidence, weight by money, and keep two ages apart (chosen)

## Decision

**Freshness is a property of each price, measured from the market's own `observed_at`.** It is not measured from our
ingestion time: re-reading yesterday's list does not make yesterday's prices fresh.

| band | default age | used in totals | UI |
|------|-------------|----------------|----|
| fresh | ≤ 6 h | yes | green "fresh · 12 min" badge |
| aging | ≤ 24 h | yes | amber badge |
| stale | ≤ 7 d | yes, **and counted in the stale share** | red badge; warning if ≥ 20 % of the total |
| expired | > 7 d | **no**: treated as missing, so a substitute is tried or the line is reported missing | n/a |

Thresholds are configuration (`Freshness:*`) and are returned with every comparison (`policy`), so the client applies
exactly the server's rules.

**Stale share is weighted by money, not line count.** One stale olive-oil price matters more than one stale bread
price. Each market reports `freshKurus / agingKurus / staleKurus / staleShare`.

**Feed health is reported separately from freshness.** A feed can be "down" while its prices are still minutes old, for
example a 10-minute outage of a live feed. A feed can also be "healthy" while serving a nightly list that is 20 hours
old. Mixing the two would be wrong in both cases. Health (`healthy / degraded / down / unknown`) answers "is the pipe
working?". Freshness answers "how old is this price?".

**Two ages, never conflated, on the device:**

1. *Result age*: when the device last got a comparison ("Showing the comparison saved 23 min ago").
2. *Price age*: how old each price was when the market stated it ("stale · 3 d").

**Freshness is computed at read time, everywhere.**

- The server never caches freshness labels. The cached comparison holds prices and `observed_at`, and each request
  re-classifies them (ADR 0003).
- The device re-classifies the saved comparison every 30 s against its own clock. A result saved as "fresh" turns
  "aging" and then "stale" while the phone stays offline. If a price in it passes expiry (`validUntil`), the UI says
  the result must be refreshed.

**Disconnection handling on the device** (Expo app, `mobile/src/sync`):

- TanStack Query with `networkMode: 'offlineFirst'`, persisted to AsyncStorage (localStorage on the web) for 7 days,
  which equals the expiry window. Keeping a result longer than its prices can be used has no value.
- NetInfo drives `onlineManager`, because React Native has no `navigator.onLine`.
- The basket is edited locally and pushed when the network returns, under a device-generated id
  (`PUT /baskets/{id}`).
- SignalR reconnects forever with capped, jittered backoff. The client's default policy stops after four attempts and
  never retries the initial start. Groups are per connection, so after every reconnect the client re-watches its
  basket and refetches the comparison: pushes sent while it was away are not replayed.
- Every resync rule is a pure function of a state transition (`resyncPlan(before, after)`) and is unit-tested.

## Consequences

- A total can include stale prices. This is deliberate: it is the best evidence available. It is always labelled, and
  the share is visible.
- Expired prices remove a market's line. The market may then become "incomplete" and rank below complete markets even
  if it looks cheaper. An incomplete total is not comparable.
- A clock that runs ahead on the feed side is clamped (`observed_at` more than 5 min in the future is rejected) instead
  of producing "fresher than now" prices.
- The device clock is trusted for ageing. A badly wrong device clock shows wrong ages. The server's `servedAt` is
  available if that ever needs correcting.
- No push replay: after a long disconnection the client does one refetch instead of receiving a backlog. That is
  simpler and always correct. The price is one extra request per reconnect.
