import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** contracts/http-api.md item shape (money in cents). */
export interface Item {
  id: string;
  name: string;
  priceCents: number;
  stock: number;
  available: boolean;
}

export interface ItemPage {
  items: Item[];
  page: number;
  pageSize: number;
  total: number;
}

/** What the admin form sends: `price` is a decimal string with at most 2 decimals. */
export interface ItemInput {
  name: string;
  stock: number | null;
  price: string;
}

/** Admin API client. Cookies and the X-XSRF-TOKEN header are handled by HttpClient. */
@Injectable({ providedIn: 'root' })
export class AdminApi {
  private readonly http = inject(HttpClient);

  /** Set by `me()`: true while the generated password is still in use. */
  readonly initialPassword = signal(false);

  login(password: string): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/admin/login', { password }));
  }

  logout(): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/admin/logout', null));
  }

  /** Checks the session (401 when signed out) and refreshes the XSRF cookie. */
  async me(): Promise<void> {
    const me = await firstValueFrom(this.http.get<{ initialPassword: boolean }>('/api/admin/me'));
    this.initialPassword.set(me.initialPassword);
  }

  async changePassword(currentPassword: string, newPassword: string): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/admin/password', { currentPassword, newPassword }));
    this.initialPassword.set(false);
  }

  items(q: string, page: number): Promise<ItemPage> {
    return firstValueFrom(
      this.http.get<ItemPage>('/api/admin/items', { params: { q, page } }),
    );
  }

  createItem(input: ItemInput): Promise<Item> {
    return firstValueFrom(this.http.post<Item>('/api/admin/items', input));
  }

  updateItem(id: string, input: ItemInput): Promise<Item> {
    return firstValueFrom(this.http.put<Item>(`/api/admin/items/${encodeURIComponent(id)}`, input));
  }

  deleteItem(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/admin/items/${encodeURIComponent(id)}`));
  }
}
