import { Component, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { formatCents, problemMessage } from '../shared/api';
import { Cart } from './cart';
import { correctCart, orderProblems } from './problems';
import { ShopApi } from './shop-api';

/** Cart with quantities, removal and checkout; server-side changes are shown for confirmation. */
@Component({
  selector: 'app-cart-page',
  imports: [RouterLink],
  template: `
    <section class="card">
      <h2>Cart</h2>
      @if (changes().length > 0) {
        <div class="notice" role="alert">
          <p>Some items changed since you added them. Nothing was ordered yet:</p>
          <ul>
            @for (m of changes(); track $index) { <li>{{ m }}</li> }
          </ul>
          @if (cart.lines().length > 0) {
            <p>Check the corrected cart below and place the order again to confirm.</p>
          }
        </div>
      }
      @if (notice()) { <p class="notice" role="status">{{ notice() }}</p> }
      @if (error()) { <p class="error" role="alert">{{ error() }}</p> }

      @if (cart.lines().length === 0) {
        <p>Your cart is empty. <a routerLink="/">Browse items</a></p>
      } @else {
        <table>
          <thead>
            <tr><th scope="col">Item</th><th scope="col">Price</th><th scope="col">Quantity</th><th scope="col">Total</th><th scope="col"><span class="visually-hidden">Remove</span></th></tr>
          </thead>
          <tbody>
            @for (line of cart.lines(); track line.itemId) {
              <tr>
                <td>{{ line.name }}</td>
                <td>{{ price(line.unitPriceCents) }}</td>
                <td>
                  <label class="visually-hidden" [for]="'qty-' + line.itemId">Quantity of {{ line.name }}</label>
                  <input class="qty" type="number" min="1" [max]="line.stock" step="1" [id]="'qty-' + line.itemId"
                         [value]="line.quantity" (change)="setQuantity(line.itemId, $event)" />
                </td>
                <td>{{ price(line.unitPriceCents * line.quantity) }}</td>
                <td><button type="button" class="danger" (click)="cart.remove(line.itemId)">Remove</button></td>
              </tr>
            }
          </tbody>
          <tfoot>
            <tr><th scope="row" colspan="3">Total</th><td colspan="2"><strong>{{ price(cart.totalCents()) }} mUSDT</strong></td></tr>
          </tfoot>
        </table>
        <button type="button" class="checkout" [disabled]="busy()" (click)="checkout()">
          {{ changes().length > 0 ? 'Confirm and place order' : 'Place order' }}
        </button>
      }
    </section>
  `,
})
export class CartPage {
  protected readonly cart = inject(Cart);
  private readonly api = inject(ShopApi);
  private readonly router = inject(Router);

  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly notice = signal('');
  protected readonly changes = signal<string[]>([]);
  protected readonly price = formatCents;

  protected setQuantity(itemId: string, event: Event): void {
    const input = event.target as HTMLInputElement;
    const change = this.cart.setQuantity(itemId, Number(input.value));
    input.value = String(change.quantity);
    this.notice.set(change.message ?? '');
  }

  protected async checkout(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    const lines = this.cart.lines();
    try {
      const order = await this.api.placeOrder(
        lines.map((l) => ({ itemId: l.itemId, quantity: l.quantity, unitPriceCents: l.unitPriceCents })),
      );
      this.cart.clear();
      await this.router.navigate(['/orders', order.reference], { state: { totalCents: order.totalCents } });
    } catch (e) {
      const problems = orderProblems(e);
      if (problems) {
        const correction = correctCart(lines, problems);
        this.cart.replace(correction.lines);
        this.changes.set(correction.messages);
      } else {
        this.error.set(problemMessage(e));
      }
    } finally {
      this.busy.set(false);
    }
  }
}
