import { Injectable, computed, signal } from '@angular/core';

/** data-model.md "Cart": lives only in the browser, under localStorage["shop.cart"]. */
export const CART_KEY = 'shop.cart';

export interface CartLine {
  itemId: string;
  name: string;
  /** The price the customer saw; the server re-checks it when ordering. */
  unitPriceCents: number;
  quantity: number;
  /** Stock when the line was last refreshed; quantities are capped at it (FR-011). */
  stock: number;
}

export interface CartItem {
  id: string;
  name: string;
  priceCents: number;
  stock: number;
}

/** Result of a change: the quantity actually set, and a message when it had to be limited. */
export interface CartChange {
  quantity: number;
  message?: string;
}

@Injectable({ providedIn: 'root' })
export class Cart {
  private readonly state = signal<CartLine[]>(load());

  readonly lines = this.state.asReadonly();
  readonly count = computed(() => this.state().reduce((n, l) => n + l.quantity, 0));
  readonly totalCents = computed(() => this.state().reduce((sum, l) => sum + l.unitPriceCents * l.quantity, 0));

  /** Adds pieces of an item, limited to its stock. Unavailable items can't be added (FR-012). */
  add(item: CartItem, quantity = 1): CartChange {
    if (item.stock <= 0) {
      return { quantity: 0, message: `${item.name} is not available.` };
    }
    const existing = this.state().find((l) => l.itemId === item.id);
    const wanted = (existing?.quantity ?? 0) + quantity;
    const line: CartLine = {
      itemId: item.id,
      name: item.name,
      unitPriceCents: item.priceCents,
      stock: item.stock,
      quantity: Math.min(wanted, item.stock),
    };
    this.save(existing ? this.state().map((l) => (l.itemId === item.id ? line : l)) : [...this.state(), line]);
    return limited(line, wanted);
  }

  /** Sets a line's quantity (whole number), limited to 1 … stock. */
  setQuantity(itemId: string, quantity: number): CartChange {
    const line = this.state().find((l) => l.itemId === itemId);
    if (!line) {
      return { quantity: 0 };
    }
    const wanted = Math.max(1, Math.floor(Number.isFinite(quantity) ? quantity : 1));
    const updated = { ...line, quantity: Math.min(wanted, line.stock) };
    this.save(this.state().map((l) => (l.itemId === itemId ? updated : l)));
    return limited(updated, wanted);
  }

  remove(itemId: string): void {
    this.save(this.state().filter((l) => l.itemId !== itemId));
  }

  /** Replaces all lines (used after the server reported changes). */
  replace(lines: CartLine[]): void {
    this.save(lines.filter((l) => l.quantity > 0));
  }

  clear(): void {
    this.save([]);
  }

  private save(lines: CartLine[]): void {
    this.state.set(lines);
    try {
      localStorage.setItem(CART_KEY, JSON.stringify(lines));
    } catch {
      // Storage full or blocked: the cart still works for this page view.
    }
  }
}

function limited(line: CartLine, wanted: number): CartChange {
  return wanted > line.quantity
    ? { quantity: line.quantity, message: `Only ${line.stock} of ${line.name} in stock.` }
    : { quantity: line.quantity };
}

/** Reads the stored cart, dropping anything malformed. */
function load(): CartLine[] {
  try {
    const raw: unknown = JSON.parse(localStorage.getItem(CART_KEY) ?? '[]');
    return Array.isArray(raw) ? raw.filter(isLine) : [];
  } catch {
    return [];
  }
}

function isLine(value: unknown): value is CartLine {
  const l = value as CartLine;
  return (
    typeof l === 'object' && l !== null &&
    typeof l.itemId === 'string' && typeof l.name === 'string' &&
    Number.isInteger(l.unitPriceCents) && l.unitPriceCents > 0 &&
    Number.isInteger(l.quantity) && l.quantity > 0 &&
    Number.isInteger(l.stock) && l.quantity <= l.stock
  );
}
