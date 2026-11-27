import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { formatCents, problemMessage } from '../../shared/api';
import { formatBaseUnits } from '../../shared/eip1193';
import { AdminApi, Order, OrderPage, PaymentPage } from '../admin-api';

/**
 * US3: orders newest first, 20 per page, with expandable lines (snapshot at ordering time); spec 005:
 * payment status with a link to the transaction, and the transfers that paid no order.
 */
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
              <td class="status">
                {{ statusLabel(order.status) }}
                @if (order.payment) {
                  <a [href]="order.payment.explorerUrl" target="_blank" rel="noopener noreferrer"
                     [title]="'Paid ' + (order.payment.paidAt | date: 'yyyy-MM-dd HH:mm:ss')">transaction</a>
                }
              </td>
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
                  <p class="muted">Pay to {{ order.walletAddress }}@if (order.payingAccount) { from {{ order.payingAccount }}}
                    @if (order.payment) { — paid {{ order.payment.paidAt | date: 'yyyy-MM-dd HH:mm:ss' }} } @else if (order.status === 'PendingPayment') { — due by {{ order.deadline | date: 'yyyy-MM-dd HH:mm' }} }</p>
                </td>
              </tr>
            }
          } @empty {
            <tr><td colspan="5">{{ loading() ? 'Loading…' : 'No orders yet.' }}</td></tr>
          }
        </tbody>
      </table>

      @if ((unmatched()?.payments ?? []).length > 0) {
        <h3>Payments not matched</h3>
        <p class="muted">Transfers to the shop's address that paid no order. The platform can't refund them; settle them with the buyer.</p>
        <table class="unmatched">
          <thead>
            <tr><th scope="col">Time</th><th scope="col">From</th><th scope="col">Amount (mUSDT)</th><th scope="col">Why</th><th scope="col">Transaction</th></tr>
          </thead>
          <tbody>
            @for (p of unmatched()!.payments; track p.tx + p.logIndex) {
              <tr>
                <td>{{ p.blockTime | date: 'yyyy-MM-dd HH:mm' }}</td>
                <td class="mono">{{ p.from }}</td>
                <td>{{ units(p.valueBaseUnits) }}</td>
                <td>{{ reasonLabel(p.reason) }}</td>
                <td><a [href]="p.explorerUrl" target="_blank" rel="noopener noreferrer">view</a></td>
              </tr>
            }
          </tbody>
        </table>
      }

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
  protected readonly unmatched = signal<PaymentPage | null>(null);
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

  protected reasonLabel(reason: string | null): string {
    switch (reason) {
      case 'amount':
        return 'Wrong amount';
      case 'account':
        return 'Unknown account';
      case 'late':
        return 'After the order expired';
      case 'duplicate':
        return 'Order already paid';
      default:
        return 'No matching order';
    }
  }

  /** Base units → "11.00"; amounts below a cent show as "0.00". */
  protected units(value: string): string {
    return formatBaseUnits(BigInt(value));
  }

  protected go(page: number): void {
    this.pageNumber.set(page);
    void this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set('');
    try {
      const [orders, unmatched] = await Promise.all([this.api.orders(this.pageNumber()), this.api.unmatchedPayments()]);
      this.page.set(orders);
      this.unmatched.set(unmatched);
      this.open.set(new Set());
    } catch (e) {
      this.error.set(problemMessage(e));
    } finally {
      this.loading.set(false);
    }
  }
}
