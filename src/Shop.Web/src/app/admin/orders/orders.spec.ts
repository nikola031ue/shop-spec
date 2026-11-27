import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AdminOrders } from './orders';

const page = {
  orders: [
    {
      reference: 'X7P33M3JT0', createdAt: '2026-11-24T09:28:08Z', status: 'PendingPayment', totalCents: 3550,
      walletAddress: '0xabc', totalBaseUnits: '35500000', payingAccount: '0xdef', deadline: '2026-11-24T09:58:08Z',
      lines: [
        { itemId: 'a', name: 'Blue T-shirt', unitPriceCents: 1500, quantity: 2 },
        { itemId: 'b', name: 'Red cap', unitPriceCents: 550, quantity: 1 },
      ],
    },
  ],
  page: 1, pageSize: 20, total: 41,
};

describe('AdminOrders', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [AdminOrders], providers: [provideHttpClient(), provideHttpClientTesting()] });
  });

  const noPayments = { payments: [], page: 1, pageSize: 20, total: 0 };

  it('lists orders, expands lines and pages', async () => {
    const fixture = TestBed.createComponent(AdminOrders);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.expectOne((r) => r.url === '/api/admin/orders' && r.params.get('page') === '1').flush(page);
    http.expectOne((r) => r.url === '/api/admin/payments' && r.params.get('matched') === 'false').flush(noPayments);
    for (let i = 0; i < 5; i++) await new Promise((r) => setTimeout(r, 0));
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.textContent).toContain('X7P33M3JT0');
    expect(el.textContent).toContain('Waiting for payment');
    expect(el.textContent).toContain('35.50');
    expect(el.textContent).not.toContain('Blue T-shirt');
    expect(el.textContent).toContain('Page 1 of 3');

    (el.querySelector('tbody button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.textContent).toContain('Blue T-shirt');
    expect(el.textContent).toContain('30.00');

    (Array.from(el.querySelectorAll('.pager button')).find((b) => b.textContent?.includes('Next')) as HTMLButtonElement).click();
    http.expectOne((r) => r.url === '/api/admin/orders' && r.params.get('page') === '2').flush({ ...page, orders: [], page: 2 });
    http.expectOne('/api/admin/payments?matched=false&page=1').flush(noPayments);
    http.verify();
  });

  it('shows paid orders with their transaction and lists transfers that paid no order (spec 005)', async () => {
    const fixture = TestBed.createComponent(AdminOrders);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    const paid = {
      ...page.orders[0], status: 'Paid',
      payment: { tx: '0xaaa', block: 7, paidAt: '2026-11-24T09:30:00Z', explorerUrl: 'https://sepolia.etherscan.io/tx/0xaaa' },
    };
    http.expectOne((r) => r.url === '/api/admin/orders').flush({ ...page, orders: [paid], total: 1 });
    http.expectOne((r) => r.url === '/api/admin/payments').flush({
      payments: [{
        tx: '0xbbb', logIndex: 0, block: 8, blockTime: '2026-11-24T09:31:00Z', from: '0x1234', to: '0xabc',
        valueBaseUnits: '10999999', orderReference: null, reason: 'amount', explorerUrl: 'https://sepolia.etherscan.io/tx/0xbbb',
      }],
      page: 1, pageSize: 20, total: 1,
    });
    for (let i = 0; i < 5; i++) await new Promise((r) => setTimeout(r, 0));
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('td.status')?.textContent).toContain('Paid');
    expect((el.querySelector('td.status a') as HTMLAnchorElement).href).toBe('https://sepolia.etherscan.io/tx/0xaaa');
    expect(el.textContent).toContain('Payments not matched');
    expect(el.querySelector('table.unmatched')?.textContent).toContain('Wrong amount');
    expect(el.querySelector('table.unmatched')?.textContent).toContain('10.99');
    http.verify();
  });
});
