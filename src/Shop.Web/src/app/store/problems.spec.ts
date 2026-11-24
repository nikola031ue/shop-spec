import { HttpErrorResponse } from '@angular/common/http';
import { CartLine } from './cart';
import { correctCart, orderProblems } from './problems';

const lines: CartLine[] = [
  { itemId: 'shirt', name: 'Blue T-shirt', unitPriceCents: 1500, quantity: 2, stock: 5 },
  { itemId: 'cap', name: 'Red cap', unitPriceCents: 500, quantity: 3, stock: 3 },
  { itemId: 'hat', name: 'Old hat', unitPriceCents: 900, quantity: 1, stock: 1 },
  { itemId: 'sock', name: 'Socks', unitPriceCents: 200, quantity: 1, stock: 9 },
];

describe('orderProblems', () => {
  it('reads the 409 problem list only', () => {
    const problems = [{ itemId: 'hat', kind: 'removed' }];
    expect(orderProblems(new HttpErrorResponse({ status: 409, error: { problems } }))).toEqual(problems);
    expect(orderProblems(new HttpErrorResponse({ status: 400, error: { problems } }))).toBeNull();
    expect(orderProblems(new HttpErrorResponse({ status: 409, error: {} }))).toBeNull();
  });
});

describe('correctCart (US2 scenario 5)', () => {
  it('maps removed, price changed and only-N-left, keeping untouched lines', () => {
    const result = correctCart(lines, [
      { itemId: 'shirt', kind: 'price', currentPriceCents: 1600, currentStock: 5 },
      { itemId: 'cap', kind: 'stock', currentStock: 1 },
      { itemId: 'hat', kind: 'removed' },
    ]);

    expect(result.messages).toEqual([
      'The price of Blue T-shirt changed from 15.00 to 16.00.',
      'Only 1 of Red cap left; the quantity was reduced.',
      'Old hat is no longer sold and was removed.',
    ]);
    expect(result.lines).toEqual([
      { itemId: 'shirt', name: 'Blue T-shirt', unitPriceCents: 1600, quantity: 2, stock: 5 },
      { itemId: 'cap', name: 'Red cap', unitPriceCents: 500, quantity: 1, stock: 1 },
      lines[3],
    ]);
  });

  it('removes sold-out items, also when their price changed', () => {
    const result = correctCart(lines, [
      { itemId: 'cap', kind: 'stock', currentStock: 0 },
      { itemId: 'shirt', kind: 'price', currentPriceCents: 1600, currentStock: 0 },
    ]);

    expect(result.lines.map((l) => l.itemId)).toEqual(['hat', 'sock']);
    expect(result.messages).toEqual(['Blue T-shirt is sold out and was removed.', 'Red cap is sold out and was removed.']);
  });

  it('reduces the quantity when the price changed and stock dropped', () => {
    const result = correctCart(lines, [{ itemId: 'shirt', kind: 'price', currentPriceCents: 1400, currentStock: 1 }]);

    expect(result.lines[0]).toEqual({ itemId: 'shirt', name: 'Blue T-shirt', unitPriceCents: 1400, quantity: 1, stock: 1 });
    expect(result.messages[0]).toBe('The price of Blue T-shirt changed from 15.00 to 14.00. Only 1 left.');
  });
});
