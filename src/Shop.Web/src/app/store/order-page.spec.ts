import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ETHEREUM, Eip1193Provider, encodeMint, encodeTransfer } from '../shared/eip1193';
import { OrderPage, paymentKey } from './order-page';
import { PublicOrder } from './shop-api';

const TOKEN = '0xdF8d5dEb736EBC79f7e99C4c90975d4b8f8d80E2';
const SHOP = '0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed';
const BUYER = '0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359';
const SEPOLIA = '0xaa36a7';
const REF = 'X7P33M3JT0';

const shop = {
  name: 'Acme',
  adminPasswordChanged: true,
  payments: { tokenAddress: TOKEN, chainId: 11155111, explorerTxUrl: 'https://sepolia.etherscan.io/tx/', mintPerCall: '1000000000' },
};

function order(overrides: Partial<PublicOrder> = {}): PublicOrder {
  return {
    reference: REF, status: 'PendingPayment', lines: [{ name: 'Shirt', unitPriceCents: 1100, quantity: 1 }],
    totalCents: 1100, totalBaseUnits: '11000000', walletAddress: SHOP, payingAccount: BUYER,
    createdAt: new Date().toISOString(), deadline: new Date(Date.now() + 29 * 60_000).toISOString(), ...overrides,
  };
}

/** A MetaMask stand-in: records every request and answers from its fields. */
class FakeMetaMask implements Eip1193Provider {
  calls: { method: string; params?: unknown[] }[] = [];
  chain = '0x1';
  acceptSwitch = true;
  account = BUYER.toLowerCase(); // MetaMask reports lowercase
  tokens = 50_000_000n;
  eth = 10n ** 16n;

  async request(args: { method: string; params?: unknown[] }): Promise<unknown> {
    this.calls.push(args);
    switch (args.method) {
      case 'eth_requestAccounts':
        return [this.account];
      case 'eth_chainId':
        return this.chain;
      case 'wallet_switchEthereumChain':
        if (!this.acceptSwitch) {
          throw { code: 4001, message: 'User rejected the request.' };
        }
        this.chain = (args.params![0] as { chainId: string }).chainId;
        return null;
      case 'eth_call':
        return '0x' + this.tokens.toString(16).padStart(64, '0');
      case 'eth_getBalance':
        return '0x' + this.eth.toString(16);
      case 'eth_sendTransaction':
        return '0x' + 'ab'.repeat(32);
      default:
        throw new Error(`unexpected ${args.method}`);
    }
  }

  sent(): { from: string; to: string; data: string }[] {
    return this.calls.filter((c) => c.method === 'eth_sendTransaction').map((c) => c.params![0] as { from: string; to: string; data: string });
  }
}

const settle = async (fixture: ComponentFixture<OrderPage>) => {
  for (let i = 0; i < 10; i++) {
    await new Promise((r) => setTimeout(r, 0));
  }
  fixture.detectChanges();
};

describe('OrderPage (spec 005 US1)', () => {
  let metamask: FakeMetaMask | null;

  async function open(o: PublicOrder = order()): Promise<{ fixture: ComponentFixture<OrderPage>; el: HTMLElement; http: HttpTestingController }> {
    TestBed.configureTestingModule({
      imports: [OrderPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: ETHEREUM, useValue: metamask }],
    });
    const fixture = TestBed.createComponent(OrderPage);
    fixture.componentRef.setInput('reference', REF);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.expectOne('/api/shop').flush(shop);
    http.expectOne(`/api/orders/${REF}`).flush(o);
    await settle(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement, http };
  }

  const click = (el: HTMLElement, selector: string) => (el.querySelector(selector) as HTMLButtonElement).click();

  beforeEach(() => {
    localStorage.clear();
    metamask = new FakeMetaMask();
  });

  it('shows the total, the address, the paying account and the time left', async () => {
    const { el } = await open();

    expect(el.textContent).toContain('11.00 mUSDT');
    expect(el.textContent).toContain(SHOP);
    expect(el.textContent).toContain(BUYER);
    expect(el.textContent).toContain('Waiting for payment');
    expect(el.querySelector('.time-left')?.textContent).toMatch(/Time left to pay: 2[89]:\d\d/);
  });

  it('switches MetaMask to Sepolia, then sends exactly the total to the shop and shows it confirming', async () => {
    const { fixture, el } = await open();

    click(el, 'button.pay');
    await settle(fixture);

    const methods = metamask!.calls.map((c) => c.method);
    expect(methods.indexOf('wallet_switchEthereumChain')).toBeLessThan(methods.indexOf('eth_sendTransaction'));
    expect(metamask!.calls.find((c) => c.method === 'wallet_switchEthereumChain')!.params).toEqual([{ chainId: SEPOLIA }]);
    expect(metamask!.sent()).toEqual([{ from: BUYER.toLowerCase(), to: TOKEN, data: encodeTransfer(SHOP, 11_000_000n) }]);
    expect(el.textContent).toContain('Payment sent, confirming');
    expect(localStorage.getItem(paymentKey(REF))).toBe('0x' + 'ab'.repeat(32));
  });

  it('sends nothing while MetaMask stays on another network (scenario 2)', async () => {
    metamask!.acceptSwitch = false;
    const { fixture, el } = await open();

    click(el, 'button.pay');
    await settle(fixture);

    expect(metamask!.sent()).toEqual([]);
    expect(el.querySelector('[role=alert]')?.textContent).toContain('rejected in MetaMask');
  });

  it('says which balance is missing and sends nothing (scenario 3)', async () => {
    metamask!.tokens = 10_000_000n;
    const first = await open();
    click(first.el, 'button.pay');
    await settle(first.fixture);
    expect(first.el.querySelector('[role=alert]')?.textContent).toContain('Not enough mUSDT');
    expect(first.el.querySelector('[role=alert]')?.textContent).toContain('10.00');

    metamask!.tokens = 50_000_000n;
    metamask!.eth = 0n;
    click(first.el, 'button.pay');
    await settle(first.fixture);
    expect(first.el.querySelector('[role=alert]')?.textContent).toContain('no Sepolia ETH for gas');
    expect(metamask!.sent()).toEqual([]);
  });

  it('refuses to pay from another account than the order', async () => {
    metamask!.account = SHOP.toLowerCase();
    const { fixture, el } = await open();

    click(el, 'button.pay');
    await settle(fixture);

    expect(el.querySelector('[role=alert]')?.textContent).toContain(`Pay from the account that placed this order (${BUYER})`);
    expect(metamask!.sent()).toEqual([]);
  });

  it('mints 1,000 test mUSDT to the connected account', async () => {
    const { fixture, el } = await open();

    click(el, 'button.mint');
    await settle(fixture);

    expect(metamask!.sent()).toEqual([{ from: BUYER.toLowerCase(), to: TOKEN, data: encodeMint(1_000_000_000n) }]);
    expect(el.querySelector('[role=status]')?.textContent).toContain('Minting 1000.00 test mUSDT');
  });

  it('keeps showing a sent payment after a reload, and Paid with the transaction once the order is paid', async () => {
    localStorage.setItem(paymentKey(REF), '0x' + 'cd'.repeat(32));
    const { fixture, el, http } = await open();
    expect(el.textContent).toContain('Payment sent, confirming');
    expect(el.querySelector('button.pay')).toBeNull();

    void fixture.componentInstance.refresh();
    const paid = order({
      status: 'Paid',
      payment: { tx: '0x' + 'cd'.repeat(32), block: 7, paidAt: new Date().toISOString(), explorerUrl: 'https://sepolia.etherscan.io/tx/0x' + 'cd'.repeat(32) },
    });
    http.expectOne(`/api/orders/${REF}`).flush(paid);
    await settle(fixture);

    expect(el.querySelector('.status')?.textContent).toContain('Paid');
    expect((el.querySelector('a[target=_blank]') as HTMLAnchorElement).href).toBe(paid.payment!.explorerUrl);
    expect(localStorage.getItem(paymentKey(REF))).toBeNull();
  });

  it('shows an expired order without pay buttons', async () => {
    const { el } = await open(order({ status: 'Expired' }));

    expect(el.textContent).toContain('expired before a payment arrived');
    expect(el.querySelector('button.pay')).toBeNull();
  });

  it('asks for MetaMask when it is missing', async () => {
    metamask = null;
    const { fixture, el } = await open();

    click(el, 'button.pay');
    await settle(fixture);

    expect(el.querySelector('[role=alert]')?.textContent).toContain('Install MetaMask');
  });
});
