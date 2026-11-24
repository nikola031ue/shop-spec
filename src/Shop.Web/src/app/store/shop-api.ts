import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Item, ItemPage } from '../admin/admin-api';

export type { Item, ItemPage };

/** One order line as sent: `unitPriceCents` is the price the customer saw (checked by the server). */
export interface OrderLineRequest {
  itemId: string;
  quantity: number;
  unitPriceCents: number;
}

export interface PlacedOrder {
  reference: string;
  status: string;
  totalCents: number;
  createdAt: string;
}

/** contracts/http-api.md 409 problem entry. */
export interface OrderProblem {
  itemId: string;
  kind: 'removed' | 'price' | 'stock';
  currentPriceCents?: number;
  currentStock?: number;
}

/** Public storefront API client. */
@Injectable({ providedIn: 'root' })
export class ShopApi {
  private readonly http = inject(HttpClient);

  /** The shop name from the Shop resource (FR-009). */
  readonly shopName = signal('');

  async loadShop(): Promise<void> {
    const shop = await firstValueFrom(this.http.get<{ name: string }>('/api/shop'));
    this.shopName.set(shop.name);
  }

  items(q: string, page: number): Promise<ItemPage> {
    return firstValueFrom(this.http.get<ItemPage>('/api/items', { params: { q, page } }));
  }

  placeOrder(lines: OrderLineRequest[]): Promise<PlacedOrder> {
    return firstValueFrom(this.http.post<PlacedOrder>('/api/orders', { lines }));
  }
}
