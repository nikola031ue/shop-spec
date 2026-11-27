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

/** Spec 005 contracts/shop-api.md: which token and chain to pay with. */
export interface PaymentsConfig {
  tokenAddress: string | null;
  chainId: number;
  explorerTxUrl: string;
  mintPerCall: string;
}

export type OrderStatus = 'PendingPayment' | 'Paid' | 'Expired';

/** Spec 005 contracts/shop-api.md public `Order`; base-unit amounts are strings. */
export interface PublicOrder {
  reference: string;
  status: OrderStatus;
  lines: { name: string; unitPriceCents: number; quantity: number }[];
  totalCents: number;
  totalBaseUnits: string;
  walletAddress: string;
  payingAccount: string | null;
  createdAt: string;
  deadline: string;
  payment?: { tx: string; block: number; paidAt: string; explorerUrl: string };
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

  /** Token and chain for paying (spec 005); null until loaded. */
  readonly payments = signal<PaymentsConfig | null>(null);

  async loadShop(): Promise<void> {
    const shop = await firstValueFrom(this.http.get<{ name: string; payments: PaymentsConfig }>('/api/shop'));
    this.shopName.set(shop.name);
    this.payments.set(shop.payments);
  }

  items(q: string, page: number): Promise<ItemPage> {
    return firstValueFrom(this.http.get<ItemPage>('/api/items', { params: { q, page } }));
  }

  /** Places an order for the connected MetaMask account (spec 005 FR-002a). */
  placeOrder(lines: OrderLineRequest[], payingAccount: string): Promise<PublicOrder> {
    return firstValueFrom(this.http.post<PublicOrder>('/api/orders', { lines, payingAccount }));
  }

  /** The order by its reference, the buyer's only handle (spec 005 FR-002). */
  order(reference: string): Promise<PublicOrder> {
    return firstValueFrom(this.http.get<PublicOrder>(`/api/orders/${encodeURIComponent(reference)}`));
  }
}
