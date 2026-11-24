import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AdminOrders } from './orders';

const page = {
  orders: [
    {
      reference: 'X7P33M3JT0', createdAt: '2026-11-24T09:28:08Z', status: 'PendingPayment', totalCents: 3550,
      walletAddress: '0xabc', lines: [
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

  it('lists orders, expands lines and pages', async () => {
    const fixture = TestBed.createComponent(AdminOrders);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.expectOne((r) => r.url === '/api/admin/orders' && r.params.get('page') === '1').flush(page);
    await fixture.whenStable();
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
    http.verify();
  });
});
