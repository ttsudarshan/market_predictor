import { Injectable, inject, signal } from '@angular/core';
import { HttpTransportType, HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { AuthService } from './auth.service';
import { Order, Quote } from './models';

export type LiveState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

/** Live prices and order events from the server's SignalR hub. */
@Injectable({ providedIn: 'root' })
export class LiveService {
  private auth = inject(AuthService);
  private connection: HubConnection | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | undefined;
  private watchers = new Map<string, number>();

  readonly state = signal<LiveState>('disconnected');
  readonly quotes = signal<Record<string, Quote>>({});
  readonly orderUpdates = new Subject<Order>();

  /** A channel's videos moved through the pipeline. */
  readonly pipelineUpdates = new Subject<string>();

  connect(): void {
    if (this.connection) return;

    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/market', {
        accessTokenFactory: () => this.auth.token() ?? '',
        transport: HttpTransportType.WebSockets | HttpTransportType.LongPolling,
      })
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (c) => Math.min(30000, 1000 * 2 ** c.previousRetryCount) })
      .configureLogging(LogLevel.None)
      .build();

    connection.on('quote', (quote: Quote) => this.setQuote(quote));
    connection.on('orderUpdated', (order: Order) => this.orderUpdates.next(order));
    connection.on('pipelineUpdated', (channelId: string) => this.pipelineUpdates.next(channelId));
    connection.onreconnecting(() => this.state.set('reconnecting'));
    connection.onreconnected(() => {
      this.state.set('connected');
      // A reconnect is a new connection on the server, so its subscriptions are gone.
      this.send('Subscribe', [...this.watchers.keys()]);
    });
    connection.onclose(() => {
      if (this.connection === connection) this.state.set('disconnected');
    });

    this.connection = connection;
    void this.start(connection);
  }

  disconnect(): void {
    clearTimeout(this.retryTimer);
    const connection = this.connection;
    this.connection = null;
    this.watchers.clear();
    this.quotes.set({});
    this.state.set('disconnected');
    void connection?.stop();
  }

  /** Requests live prices for the symbols. Call the returned function to stop. */
  watch(symbols: string[]): () => void {
    const unique = [...new Set(symbols)];
    const added = unique.filter((s) => {
      const count = this.watchers.get(s) ?? 0;
      this.watchers.set(s, count + 1);
      return count === 0;
    });
    if (added.length) this.send('Subscribe', added);

    return () => {
      const removed = unique.filter((s) => {
        const count = (this.watchers.get(s) ?? 1) - 1;
        if (count <= 0) this.watchers.delete(s);
        else this.watchers.set(s, count);
        return count <= 0;
      });
      if (removed.length) this.send('Unsubscribe', removed);
    };
  }

  setQuote(quote: Quote): void {
    this.quotes.update((all) => ({ ...all, [quote.symbol]: quote }));
  }

  private async start(connection: HubConnection): Promise<void> {
    this.state.set('connecting');
    try {
      await connection.start();
      if (this.connection !== connection) return;
      this.state.set('connected');
      this.send('Subscribe', [...this.watchers.keys()]);
    } catch {
      // Automatic reconnect only covers connections that started, so retry the first one here.
      if (this.connection !== connection) return;
      this.state.set('reconnecting');
      this.retryTimer = setTimeout(() => void this.start(connection), 5000);
    }
  }

  private send(method: 'Subscribe' | 'Unsubscribe', symbols: string[]): void {
    if (symbols.length && this.connection?.state === HubConnectionState.Connected) {
      this.connection.invoke(method, symbols).catch(() => {
        /* the next reconnect re-sends subscriptions */
      });
    }
  }
}
