import { HttpErrorResponse } from '@angular/common/http';
import { formatCents } from '../shared/api';
import { CartLine } from './cart';
import { OrderProblem } from './shop-api';

/** What the customer is told, and the corrected cart they can confirm (US2 scenario 5). */
export interface CartCorrection {
  messages: string[];
  lines: CartLine[];
}

/** The 409 problem list of a failed order, or null for any other error. */
export function orderProblems(error: unknown): OrderProblem[] | null {
  if (error instanceof HttpErrorResponse && error.status === 409 && Array.isArray(error.error?.problems)) {
    return error.error.problems as OrderProblem[];
  }
  return null;
}

/**
 * Applies the server's problems to the cart: removed and sold-out items leave the cart, changed
 * prices are updated, and quantities drop to what is left. Lines without a problem are kept.
 */
export function correctCart(lines: CartLine[], problems: OrderProblem[]): CartCorrection {
  const byItem = new Map(problems.map((p) => [p.itemId, p]));
  const messages: string[] = [];
  const corrected: CartLine[] = [];
  for (const line of lines) {
    const problem = byItem.get(line.itemId);
    if (!problem) {
      corrected.push(line);
      continue;
    }
    switch (problem.kind) {
      case 'removed':
        messages.push(`${line.name} is no longer sold and was removed.`);
        break;
      case 'price': {
        const price = problem.currentPriceCents ?? line.unitPriceCents;
        const stock = problem.currentStock ?? line.stock;
        if (stock <= 0) {
          messages.push(`${line.name} is sold out and was removed.`);
          break;
        }
        const quantity = Math.min(line.quantity, stock);
        messages.push(
          `The price of ${line.name} changed from ${formatCents(line.unitPriceCents)} to ${formatCents(price)}.` +
            (quantity < line.quantity ? ` Only ${stock} left.` : ''),
        );
        corrected.push({ ...line, unitPriceCents: price, stock, quantity });
        break;
      }
      case 'stock': {
        const stock = problem.currentStock ?? 0;
        if (stock <= 0) {
          messages.push(`${line.name} is sold out and was removed.`);
        } else {
          messages.push(`Only ${stock} of ${line.name} left; the quantity was reduced.`);
          corrected.push({ ...line, stock, quantity: Math.min(line.quantity, stock) });
        }
        break;
      }
    }
  }
  return { messages, lines: corrected };
}
