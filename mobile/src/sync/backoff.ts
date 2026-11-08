/**
 * Reconnect delays for SignalR. The client's default automatic reconnect waits 0, 2, 10 and 30 s and then gives up
 * for good, and it never retries the initial start. A phone in a basement or on a train needs neither limit: retry
 * forever with capped exponential backoff and jitter, so thousands of clients do not reconnect in lockstep after a
 * server restart.
 */
export function reconnectDelayMs(attempt: number, random: () => number = Math.random): number {
  if (attempt <= 0) return 0;
  const base = Math.min(30_000, 1_000 * 2 ** (attempt - 1)); // 1 s, 2 s, 4 s … capped at 30 s
  const jitter = 0.5 + random() * 0.5; // 50–100 % of the base ("equal jitter")
  return Math.round(base * jitter);
}

/** Adapter for @microsoft/signalr's IRetryPolicy: never returns null, so it never stops trying. */
export const foreverRetryPolicy = {
  nextRetryDelayInMilliseconds(context: { previousRetryCount: number }): number {
    return reconnectDelayMs(context.previousRetryCount + 1);
  },
};
