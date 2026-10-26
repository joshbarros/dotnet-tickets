import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiService } from './core/api.service';
import { Order, OrderStatus, LogEntry, Tier } from './core/models';

const BADGES: Record<OrderStatus, string> = {
  Requested: 'bg-slate-100 text-slate-700 ring-slate-300',
  Held: 'bg-amber-50 text-amber-800 ring-amber-300',
  Sold: 'bg-emerald-50 text-emerald-800 ring-emerald-300',
  Refunded: 'bg-sky-50 text-sky-800 ring-sky-300',
  Rejected: 'bg-rose-50 text-rose-800 ring-rose-300',
};

@Component({
  selector: 'app-root',
  imports: [DatePipe],
  templateUrl: './app.component.html',
})
export class AppComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly tiers = signal<Tier[]>([]);
  readonly selectedCode = signal('');
  readonly orders = signal<Order[]>([]);
  readonly log = signal<LogEntry[]>([]);
  readonly logFor = signal<string | null>(null);
  readonly error = signal('');

  readonly code = signal('');
  readonly name = signal('Night One');
  readonly section = signal('Floor');
  readonly venue = signal('Arena Norte');
  readonly price = signal('5000');
  readonly capacity = signal('12');

  readonly reference = signal('');
  readonly tickets = signal('1');

  readonly selected = computed(() => this.tiers().find((s) => s.code === this.selectedCode()));

  ngOnInit(): void {
    void this.refresh();
    const timer = setInterval(() => void this.refresh(), 1000);
    this.destroyRef.onDestroy(() => clearInterval(timer));
  }

  badge(status: OrderStatus): string {
    return BADGES[status];
  }

  money(cents: number): string {
    return (cents / 100).toLocaleString('en-US', { style: 'currency', currency: 'USD' });
  }

  async refresh(): Promise<void> {
    try {
      const tiers = await this.api.tiers();
      this.tiers.set(tiers);
      if (!this.selectedCode() && tiers.length > 0) this.selectedCode.set(tiers[0].code);
      if (this.selectedCode()) this.orders.set(await this.api.orders(this.selectedCode()));
      const id = this.logFor();
      if (id) this.log.set(await this.api.events(id));
    } catch {
      this.error.set('The API is not reachable.');
      return;
    }
    if (this.error() === 'The API is not reachable.') this.error.set('');
  }

  async select(code: string): Promise<void> {
    this.selectedCode.set(code);
    this.logFor.set(null);
    this.log.set([]);
    await this.refresh();
  }

  async openTier(): Promise<void> {
    await this.run(async () => {
      const code = this.code();
      const tier = await this.api.openTier({
        code,
        name: this.name(),
        section: this.section(),
        venue: this.venue(),
        ticketsTotal: Number(this.capacity()),
        priceCents: Number(this.price()),
      });
      if (this.code() === code) this.code.set('');
      this.selectedCode.set(tier.code);
    });
  }

  async book(): Promise<void> {
    await this.run(async () => {
      const reference = this.reference();
      await this.api.book({
        tierCode: this.selectedCode(),
        reference,
        tickets: Number(this.tickets()),
      });
      if (this.reference() === reference) this.reference.set('');
    });
  }

  async refund(order: Order): Promise<void> {
    await this.run(() => this.api.refund(order.id));
  }

  async showLog(order: Order): Promise<void> {
    this.logFor.set(order.id);
    this.log.set([]);
    await this.refresh();
  }

  canRefund(order: Order): boolean {
    return order.status === 'Held' || order.status === 'Sold';
  }

  private async run(action: () => Promise<unknown>): Promise<void> {
    this.error.set('');
    try {
      await action();
    } catch (e: unknown) {
      const body = (e as { error?: { title?: string } }).error;
      this.error.set(body?.title ?? 'The request failed.');
    }
    await this.refresh();
  }

  field(signalRef: { set(value: string): void }, event: Event): void {
    signalRef.set((event.target as HTMLInputElement).value);
  }
}
