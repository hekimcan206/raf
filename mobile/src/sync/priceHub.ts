import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { API_URL } from '../api/client';
import type { FeedHealthChangedEvent, PriceChangedEvent } from '../domain/types';
import { foreverRetryPolicy, reconnectDelayMs } from './backoff';

export interface PriceHubCallbacks {
  onPriceChanged: (e: PriceChangedEvent) => void;
  onFeedHealthChanged: (e: FeedHealthChangedEvent) => void;
  onState: (state: 'connecting' | 'connected' | 'reconnecting' | 'disconnected') => void;
}

/**
 * Thin wrapper around the SignalR client that encodes the reconnect contract with the server:
 * group membership is per connection, so after every (re)connect the current basket's products are watched again.
 */
export class PriceHub {
  private readonly connection: HubConnection;
  private watched: string[] = [];
  private stopped = false;
  private startAttempt = 0;

  constructor(private readonly callbacks: PriceHubCallbacks) {
    this.connection = new HubConnectionBuilder()
      .withUrl(`${API_URL}/hubs/prices`)
      .withAutomaticReconnect(foreverRetryPolicy)
      .configureLogging(LogLevel.Warning)
      .build();
    this.connection.on('priceChanged', callbacks.onPriceChanged);
    this.connection.on('feedHealthChanged', callbacks.onFeedHealthChanged);
    this.connection.onreconnecting(() => callbacks.onState('reconnecting'));
    this.connection.onreconnected(async () => {
      await this.rewatch();
      callbacks.onState('connected');
    });
    this.connection.onclose(() => {
      callbacks.onState('disconnected');
      if (!this.stopped) void this.start();
    });
  }

  /** Automatic reconnect does not cover the initial start, so it gets its own retry loop. */
  async start(): Promise<void> {
    this.stopped = false;
    while (!this.stopped && this.connection.state === HubConnectionState.Disconnected) {
      this.callbacks.onState('connecting');
      try {
        await this.connection.start();
        this.startAttempt = 0;
        await this.rewatch();
        this.callbacks.onState('connected');
        return;
      } catch {
        this.callbacks.onState('disconnected');
        this.startAttempt += 1;
        await new Promise((r) => setTimeout(r, reconnectDelayMs(this.startAttempt)));
      }
    }
  }

  async watch(productIds: string[]): Promise<void> {
    const previous = this.watched;
    this.watched = [...new Set(productIds)];
    if (this.connection.state !== HubConnectionState.Connected) return; // applied on (re)connect
    const removed = previous.filter((id) => !this.watched.includes(id));
    if (removed.length > 0) await this.connection.invoke('Unwatch', removed);
    await this.rewatch();
  }

  async stop(): Promise<void> {
    this.stopped = true;
    await this.connection.stop();
  }

  private async rewatch(): Promise<void> {
    if (this.watched.length > 0 && this.connection.state === HubConnectionState.Connected) {
      await this.connection.invoke('Watch', this.watched);
    }
  }
}
