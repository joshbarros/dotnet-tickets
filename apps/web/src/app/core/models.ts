export interface Tier {
  id: string;
  code: string;
  name: string;
  section: string;
  venue: string;
  showDate: string;
  priceCents: number;
  ticketsTotal: number;
  ticketsLeft: number;
}

export type OrderStatus = 'Requested' | 'Held' | 'Sold' | 'Refunded' | 'Rejected';

export interface Order {
  id: string;
  tierCode: string;
  reference: string;
  tickets: number;
  totalCents: number;
  status: OrderStatus;
  reason: string | null;
  ticketNumber: string | null;
  createdAt: string;
  updatedAt: string;
  ticketsReleasedAt: string | null;
}

export interface LogEntry {
  id: string;
  type: string;
  module: string;
  occurredAt: string;
}
