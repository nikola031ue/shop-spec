import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { formatCents, problemMessage } from '../shared/api';
import { Cart } from './cart';
import { Item, ItemPage, ShopApi } from './shop-api';

/** Item list with search and paging; available items can be added to the cart (FR-009–FR-012). */
@Component({
  selector: 'app-storefront',
  imports: [FormsModule],
  template: `
    <section class="card">
      <div class="toolbar">
        <h2>Items</h2>
        <form class="search" role="search" (ngSubmit)="search()">
          <label for="q" class="visually-hidden">Search items</label>
          <input id="q" name="q" type="search" placeholder="Search by name" [(ngModel)]="query" />
          <button type="submit">Search</button>
        </form>
      </div>

      @if (notice()) { <p class="notice" role="status">{{ notice() }}</p> }
      @if (error()) { <p class="error" role="alert">{{ error() }}</p> }

      <ul class="items">
        @for (item of page()?.items ?? []; track item.id) {
          <li class="item" [class.unavailable]="!item.available">
            <span class="name">{{ item.name }}</span>
            <span class="price">{{ price(item.priceCents) }} mUSDT</span>
            @if (item.available) {
              <button type="button" (click)="add(item)">Add to cart</button>
            } @else {
              <span class="tag">Unavailable</span>
            }
          </li>
        } @empty {
          <li>{{ loading() ? 'Loading…' : 'No items found.' }}</li>
        }
      </ul>

      @if (pages() > 1) {
        <div class="pager">
          <button type="button" class="secondary" [disabled]="pageNumber() <= 1" (click)="go(pageNumber() - 1)">Previous</button>
          <span>Page {{ pageNumber() }} of {{ pages() }}</span>
          <button type="button" class="secondary" [disabled]="pageNumber() >= pages()" (click)="go(pageNumber() + 1)">Next</button>
        </div>
      }
    </section>
  `,
})
export class Storefront implements OnInit {
  private readonly api = inject(ShopApi);
  private readonly cart = inject(Cart);

  protected query = '';
  protected readonly page = signal<ItemPage | null>(null);
  protected readonly pageNumber = signal(1);
  protected readonly loading = signal(false);
  protected readonly error = signal('');
  protected readonly notice = signal('');
  protected readonly pages = computed(() => {
    const p = this.page();
    return p ? Math.max(1, Math.ceil(p.total / p.pageSize)) : 1;
  });
  protected readonly price = formatCents;

  ngOnInit(): void {
    void this.load();
  }

  protected search(): void {
    this.pageNumber.set(1);
    void this.load();
  }

  protected go(page: number): void {
    this.pageNumber.set(page);
    void this.load();
  }

  protected add(item: Item): void {
    const change = this.cart.add(item);
    this.notice.set(change.message ?? `${item.name} added to the cart.`);
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set('');
    try {
      this.page.set(await this.api.items(this.query.trim(), this.pageNumber()));
    } catch (e) {
      this.error.set(problemMessage(e));
    } finally {
      this.loading.set(false);
    }
  }
}
