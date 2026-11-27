import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { ETHEREUM, Eip1193Provider } from '../shared/eip1193';
import { Cart } from './cart';
import { CartPage } from './cart-page';

const BUYER = '0xfb6916095ca1df60bb79ce92ce3ea74c37c5d359';

const settle = async (fixture: ComponentFixture<CartPage>) => {
  for (let i = 0; i < 5; i++) {
    await new Promise((r) => setTimeout(r, 0));
  }
  fixture.detectChanges();
};

describe('CartPage checkout with MetaMask (spec 005 FR-002a, FR-005a)', () => {
  let http: HttpTestingController;
  let requests: string[];

  function setup(provider: Eip1193Provider | null) {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [CartPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: ETHEREUM, useValue: provider }],
    });
    TestBed.inject(Cart).add({ id: 'a', name: 'Shirt', priceCents: 1100, stock: 3 });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(CartPage);
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  const metamask: Eip1193Provider = {
    request: async ({ method }) => {
      requests.push(method);
      if (method === 'eth_requestAccounts') {
        return [BUYER];
      }
      throw new Error(method);
    },
  };

  beforeEach(() => (requests = []));

  it('connects MetaMask first, then places the order for that account and opens it', async () => {
    const { fixture, el } = setup(metamask);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    expect(el.querySelector('button.checkout')?.textContent).toContain('Connect MetaMask');

    (el.querySelector('button.checkout') as HTMLButtonElement).click();
    await settle(fixture);
    expect(requests).toEqual(['eth_requestAccounts']);
    expect(el.textContent).toContain(BUYER);

    (el.querySelector('button.checkout') as HTMLButtonElement).click();
    const req = http.expectOne('/api/orders');
    expect(req.request.body).toEqual({ lines: [{ itemId: 'a', quantity: 1, unitPriceCents: 1100 }], payingAccount: BUYER });
    req.flush({ reference: 'X7P33M3JT0' }, { status: 201, statusText: 'Created' });
    await settle(fixture);

    expect(navigate).toHaveBeenCalledWith(['/orders', 'X7P33M3JT0']);
    expect(TestBed.inject(Cart).lines()).toEqual([]);
  });

  it('links to the waiting order when the account already has one', async () => {
    const { fixture, el } = setup(metamask);
    (el.querySelector('button.checkout') as HTMLButtonElement).click();
    await settle(fixture);

    (el.querySelector('button.checkout') as HTMLButtonElement).click();
    http.expectOne('/api/orders').flush(
      { title: 'This account already has order AAAAAAAAAA waiting for payment.', reason: 'waitingOrder', reference: 'AAAAAAAAAA' },
      { status: 409, statusText: 'Conflict' },
    );
    await settle(fixture);

    const alert = el.querySelector('[role=alert]')!;
    expect(alert.textContent).toContain('already has order AAAAAAAAAA');
    expect(alert.querySelector('a')?.getAttribute('href')).toBe('/orders/AAAAAAAAAA');
    expect(TestBed.inject(Cart).lines()).toHaveLength(1);
  });

  it('asks for MetaMask when it is missing', async () => {
    const { fixture, el } = setup(null);

    (el.querySelector('button.checkout') as HTMLButtonElement).click();
    await settle(fixture);

    expect(el.querySelector('[role=alert]')?.textContent).toContain('Install MetaMask');
    http.expectNone('/api/orders');
  });
});
