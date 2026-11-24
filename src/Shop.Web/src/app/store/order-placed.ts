import { Component, inject, input } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { formatCents } from '../shared/api';

/** Order confirmation: the reference the customer pays with later (spec 005) and the total. */
@Component({
  selector: 'app-order-placed',
  imports: [RouterLink],
  template: `
    <section class="card narrow">
      <h2>Order placed</h2>
      <p>Your order reference:</p>
      <p class="reference">{{ reference() }}</p>
      @if (totalCents !== null) {
        <p>Total: <strong>{{ price(totalCents) }} mUSDT</strong></p>
      }
      <p>Status: waiting for payment. Keep the reference; you need it to pay.</p>
      <a routerLink="/">Continue shopping</a>
    </section>
  `,
})
export class OrderPlaced {
  /** Route parameter (component input binding). */
  readonly reference = input.required<string>();

  /** Passed in navigation state by the cart; absent after a reload. */
  protected readonly totalCents: number | null = readTotal(inject(Router));
  protected readonly price = formatCents;
}

function readTotal(router: Router): number | null {
  const total = router.currentNavigation()?.extras.state?.['totalCents'] ?? history.state?.totalCents;
  return typeof total === 'number' ? total : null;
}
