import { CART_KEY, Cart, CartItem, MAX_QUANTITY_PER_LINE } from './cart';

const shirt: CartItem = { id: 'a', name: 'Blue T-shirt', priceCents: 1500, stock: 3 };
const cap: CartItem = { id: 'b', name: 'Red cap', priceCents: 550, stock: 10 };

describe('Cart', () => {
  beforeEach(() => localStorage.clear());

  it('adds items, sums quantities and totals (US2 scenario 2)', () => {
    const cart = new Cart();
    cart.add(shirt, 2);
    cart.add(cap);
    cart.add(cap);

    expect(cart.lines().map((l) => [l.name, l.quantity])).toEqual([['Blue T-shirt', 2], ['Red cap', 2]]);
    expect(cart.count()).toBe(4);
    expect(cart.totalCents()).toBe(2 * 1500 + 2 * 550);
  });

  it('survives a reload through localStorage["shop.cart"]', () => {
    new Cart().add(shirt, 2);

    const reloaded = new Cart();

    expect(reloaded.lines()).toEqual([{ itemId: 'a', name: 'Blue T-shirt', unitPriceCents: 1500, quantity: 2, stock: 3 }]);
    expect(JSON.parse(localStorage.getItem(CART_KEY)!)).toHaveLength(1);
  });

  it('limits quantities to the stock and says why (US2 scenario 3)', () => {
    const cart = new Cart();

    const added = cart.add(shirt, 5);
    expect(added).toEqual({ quantity: 3, message: 'Only 3 of Blue T-shirt in stock.' });

    expect(cart.setQuantity('a', 10).quantity).toBe(3);
    expect(cart.setQuantity('a', 0).quantity).toBe(1);
    expect(cart.setQuantity('a', 2.7)).toEqual({ quantity: 2 });
  });

  it('limits a line to the server maximum per order', () => {
    const cart = new Cart();
    const plenty: CartItem = { id: 'p', name: 'Pens', priceCents: 10, stock: 5000 };

    expect(cart.add(plenty, 1200)).toEqual({ quantity: MAX_QUANTITY_PER_LINE, message: 'At most 1000 of Pens per order.' });
    expect(cart.setQuantity('p', 4000).quantity).toBe(MAX_QUANTITY_PER_LINE);
    expect(new Cart().lines()[0].quantity).toBe(MAX_QUANTITY_PER_LINE);
  });

  it('refuses unavailable items (FR-012)', () => {
    const cart = new Cart();

    const change = cart.add({ ...shirt, stock: 0 });

    expect(change.quantity).toBe(0);
    expect(change.message).toContain('not available');
    expect(cart.lines()).toEqual([]);
  });

  it('removes lines and clears', () => {
    const cart = new Cart();
    cart.add(shirt);
    cart.add(cap);

    cart.remove('a');
    expect(cart.lines().map((l) => l.itemId)).toEqual(['b']);

    cart.clear();
    expect(cart.lines()).toEqual([]);
    expect(localStorage.getItem(CART_KEY)).toBe('[]');
  });

  it('ignores malformed stored data', () => {
    localStorage.setItem(CART_KEY, '{not json');
    expect(new Cart().lines()).toEqual([]);

    localStorage.setItem(CART_KEY, JSON.stringify([
      { itemId: 'a', name: 'ok', unitPriceCents: 100, quantity: 1, stock: 2 },
      { itemId: 'b', name: 'too many', unitPriceCents: 100, quantity: 5, stock: 2 },
      { itemId: 'c', name: 'bad price', unitPriceCents: -1, quantity: 1, stock: 2 },
      'junk',
    ]));
    expect(new Cart().lines().map((l) => l.itemId)).toEqual(['a']);
  });
});
