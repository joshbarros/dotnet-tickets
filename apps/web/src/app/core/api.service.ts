import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Order, LogEntry, Tier } from './models';

export interface NewTier {
  code: string;
  name: string;
  section: string;
  venue: string;
  ticketsTotal: number;
  priceCents: number;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  tiers = () => firstValueFrom(this.http.get<Tier[]>('/api/tiers'));
  openTier = (body: NewTier) => firstValueFrom(this.http.post<Tier>('/api/tiers', body));

  orders = (tierCode: string) =>
    firstValueFrom(this.http.get<Order[]>('/api/orders', { params: { tierCode } }));
  book = (body: { tierCode: string; reference: string; tickets: number }) =>
    firstValueFrom(this.http.post<Order>('/api/orders', body));
  refund = (id: string) => firstValueFrom(this.http.post<Order>(`/api/orders/${id}/refund`, {}));
  events = (id: string) => firstValueFrom(this.http.get<LogEntry[]>(`/api/orders/${id}/events`));
}
