import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { formatCents, problemMessage } from '../../shared/api';
import { AdminApi, Order, OrderPage } from '../admin-api';

/** US3: orders newest first, 20 per page, with expandable lines (snapshot at ordering time). */
@Component({
  selector: 'app-admin-orders',
  imports: [DatePipe],
  template: `
    <section class="card">
      <h2>Orders</h2>
      @if (error()) { <p class="error" role="alert">{{ error() }}</p> }

      <table class="orders">
        <thead>
          <tr>
            <th scope="col">Reference</th><th scope="col">Time</th><th scope="col">Status</th>
            <th scope="col">Total (mUSDT)</th><th scope="col"><span class="visually-hidden">Lines</span></th>
          </tr>
        </thead>
        <tbody>
          @for (order of page()?.orders ?? []; track order.reference) {
            <tr>
              <td class="mono">{{ order.reference }}</td>
              <td>{{ order.createdAt | date: 'yyyy-MM-dd HH:mm' }}</td>
              <td>{{ statusLabel(order.status) }}</td>
              <td>{{ price(order.totalCents) }}</td>
              <td>
                <button type="button" class="secondary" [attr.aria-expanded]="isOpen(order)"
                        [attr.aria-controls]="'lines-' + order.reference" (click)="toggle(order)">
                  {{ isOpen(order) ? 'Hide' : 'Show' }} {{ order.lines.length }} line{{ order.lines.length === 1 ? '' : 's' }}
                </button>
              </td>
            </tr>
            @if (isOpen(order)) {
              <tr class="lines" [id]="'lines-' + order.reference">
                <td colspan="5">
                  <table>
                    <thead>
                      <tr><th scope="col">Item</th><th scope="col">Unit price</th><th scope="col">Quantity</th><th scope="col">Line total</th></tr>
                    </thead>
                    <tbody>
                      @for (line of order.lines; track line.itemId) {
                        <tr>
                          <td>{{ line.name }}</td>
                          <td>{{ price(line.unitPriceCents) }}</td>
                          <td>{{ line.quantity }}</td>
                          <td>{{ price(line.unitPriceCents * line.quantity) }}</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                  <p class="muted">Pay to {{ order.walletAddress }}</p>
                </td>
              </tr>
            }
          } @empty {
            <tr><td colspan="5">{{ loading() ? 'Loading…' : 'No orders yet.' }}</td></tr>
          }
        </tbody>
      </table>

      @if (pages() > 1) {
        <div class="pager">
          <button type="button" class="secondary" [disabled]="pageNumber() <= 1" (click)="go(pageNumber() - 1)">Previous</button>
          <span>Page {{ pageNumber() }} of {{ pages() }} ({{ page()?.total }} orders)</span>
          <button type="button" class="secondary" [disabled]="pageNumber() >= pages()" (click)="go(pageNumber() + 1)">Next</button>
        </div>
      }
    </section>
  `,
})
export class AdminOrders implements OnInit {
  private readonly api = inject(AdminApi);

  protected readonly page = signal<OrderPage | null>(null);
  protected readonly pageNumber = signal(1);
  protected readonly loading = signal(false);
  protected readonly error = signal('');
  private readonly open = signal<ReadonlySet<string>>(new Set());
  protected readonly pages = computed(() => {
    const p = this.page();
    return p ? Math.max(1, Math.ceil(p.total / p.pageSize)) : 1;
  });
  protected readonly price = formatCents;

  ngOnInit(): void {
    void this.load();
  }

  protected isOpen(order: Order): boolean {
    return this.open().has(order.reference);
  }

  protected toggle(order: Order): void {
    const next = new Set(this.open());
    if (!next.delete(order.reference)) {
      next.add(order.reference);
    }
    this.open.set(next);
  }

  protected statusLabel(status: string): string {
    return status === 'PendingPayment' ? 'Waiting for payment' : status;
  }

  protected go(page: number): void {
    this.pageNumber.set(page);
    void this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set('');
    try {
      this.page.set(await this.api.orders(this.pageNumber()));
      this.open.set(new Set());
    } catch (e) {
      this.error.set(problemMessage(e));
    } finally {
      this.loading.set(false);
    }
  }
}
