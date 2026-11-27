import { DatePipe } from '@angular/common';
import { Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { formatCents, problemMessage } from '../shared/api';
import { formatBaseUnits, sameAddress } from '../shared/eip1193';
import { PublicOrder, ShopApi } from './shop-api';
import { Wallet, walletMessage } from './wallet';

/** localStorage key for the hash MetaMask returned for this order's payment (spec 005 FR-006). */
export const paymentKey = (reference: string) => `shop.payment.${reference}`;

/** How often the page asks for the order while it waits for payment (contracts/shop-api.md). */
export const POLL_MS = 5000;

/**
 * The order page (spec 005 US1, FR-002, FR-006): what to pay, where and from which account, the time
 * left and the status; "Pay with MetaMask" and "Get test mUSDT". The page polls the order while it
 * waits; the platform marks it paid from the chain itself, so closing the tab loses nothing.
 */
@Component({
  selector: 'app-order-page',
  imports: [DatePipe, RouterLink],
  template: `
    <section class="card narrow">
      <h2>Order <span class="mono">{{ reference() }}</span></h2>
      @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
      @if (notice()) { <p class="notice" role="status">{{ notice() }}</p> }

      @if (order(); as o) {
        <dl class="facts">
          <dt>Total</dt><dd><strong>{{ amount(o) }} mUSDT</strong></dd>
          <dt>Pay to</dt><dd class="mono">{{ o.walletAddress }}</dd>
          <dt>From account</dt><dd class="mono">{{ o.payingAccount ?? 'not set (order placed before payments)' }}</dd>
          <dt>Status</dt><dd class="status">{{ statusText() }}</dd>
        </dl>

        @switch (o.status) {
          @case ('Paid') {
            <p>Paid {{ o.payment?.paidAt | date: 'yyyy-MM-dd HH:mm:ss' }}.
              <a [href]="o.payment?.explorerUrl" target="_blank" rel="noopener noreferrer">View the payment</a></p>
          }
          @case ('Expired') {
            <p>This order expired before a payment arrived; its items went back on sale.</p>
          }
          @default {
            <p class="time-left">{{ timeLeft() }}</p>
            @if (pendingTx(); as tx) {
              <p class="notice" role="status">Payment sent, confirming (about a minute).
                @if (explorer()) { <a [href]="explorer() + tx" target="_blank" rel="noopener noreferrer">View the transaction</a> }
              </p>
            } @else if (o.payingAccount) {
              <button type="button" class="pay" [disabled]="busy()" (click)="pay()">Pay with MetaMask</button>
              <button type="button" class="secondary mint" [disabled]="busy()" (click)="mint()">Get test mUSDT</button>
              <p class="muted">Only gas is paid in Sepolia ETH; the price is paid in mUSDT.</p>
            }
          }
        }
      } @else if (loading()) {
        <p>Loading…</p>
      }
      <a routerLink="/">Continue shopping</a>
    </section>
  `,
})
export class OrderPage implements OnInit {
  /** Route parameter (component input binding). */
  readonly reference = input.required<string>();

  private readonly api = inject(ShopApi);
  private readonly wallet = inject(Wallet);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly order = signal<PublicOrder | null>(null);
  protected readonly loading = signal(true);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly notice = signal('');
  protected readonly pendingTx = signal<string | null>(null);
  protected readonly now = signal(Date.now());
  protected readonly explorer = computed(() => this.api.payments()?.explorerTxUrl ?? '');

  private poll: ReturnType<typeof setTimeout> | undefined;
  private destroyed = false;

  protected readonly statusText = computed(() => {
    switch (this.order()?.status) {
      case 'Paid':
        return 'Paid';
      case 'Expired':
        return 'Expired';
      default:
        return this.pendingTx() ? 'Payment sent, confirming' : 'Waiting for payment';
    }
  });

  protected readonly timeLeft = computed(() => {
    const o = this.order();
    if (!o) {
      return '';
    }
    const ms = Date.parse(o.deadline) - this.now();
    if (ms <= 0) {
      return 'The time to pay is over; the order expires shortly.';
    }
    const minutes = Math.floor(ms / 60000);
    const seconds = Math.floor((ms % 60000) / 1000);
    return `Time left to pay: ${minutes}:${seconds.toString().padStart(2, '0')}`;
  });

  constructor() {
    const clock = setInterval(() => this.now.set(Date.now()), 1000);
    this.destroyRef.onDestroy(() => {
      this.destroyed = true;
      clearInterval(clock);
      clearTimeout(this.poll);
    });
  }

  ngOnInit(): void {
    this.pendingTx.set(readStorage(paymentKey(this.reference())));
    if (!this.api.payments()) {
      this.api.loadShop().catch(() => undefined);
    }
    void this.refresh();
  }

  protected amount(o: PublicOrder): string {
    return o.totalBaseUnits ? formatBaseUnits(BigInt(o.totalBaseUnits)) : formatCents(o.totalCents);
  }

  /** Loads the order; keeps polling while it waits for payment. */
  async refresh(): Promise<void> {
    try {
      const order = await this.api.order(this.reference());
      this.order.set(order);
      if (order.status !== 'PendingPayment') {
        removeStorage(paymentKey(order.reference));
        this.pendingTx.set(null);
      }
    } catch (e) {
      this.error.set(problemMessage(e));
    } finally {
      this.loading.set(false);
    }
    if (!this.destroyed && (this.order()?.status ?? 'PendingPayment') === 'PendingPayment') {
      this.poll = setTimeout(() => void this.refresh(), POLL_MS);
    }
  }

  /** US1: switch to Sepolia, check account and balances, then send exactly the total. */
  protected async pay(): Promise<void> {
    const order = this.order();
    if (!order?.payingAccount) {
      return;
    }
    await this.withWallet(async (token, chainId) => {
      const account = this.wallet.account() ?? (await this.wallet.connect());
      await this.wallet.ensureChain(chainId);
      if (!sameAddress(account, order.payingAccount)) {
        this.error.set(`Pay from the account that placed this order (${order.payingAccount}): switch accounts in MetaMask.`);
        return;
      }
      const amount = BigInt(order.totalBaseUnits);
      const [tokens, eth] = await Promise.all([this.wallet.tokenBalance(token, account), this.wallet.ethBalance(account)]);
      if (tokens < amount) {
        this.error.set(`Not enough mUSDT: this account has ${formatBaseUnits(tokens)}, the order needs ${formatBaseUnits(amount)}. Use "Get test mUSDT".`);
        return;
      }
      if (eth === 0n) {
        this.error.set('This account has no Sepolia ETH for gas. Get some from a Sepolia faucet.');
        return;
      }
      const tx = await this.wallet.transfer(token, account, order.walletAddress, amount);
      writeStorage(paymentKey(order.reference), tx);
      this.pendingTx.set(tx);
    });
  }

  /** FR-017: mint 1,000 test mUSDT to the connected account. */
  protected async mint(): Promise<void> {
    await this.withWallet(async (token, chainId, mintPerCall) => {
      const account = this.wallet.account() ?? (await this.wallet.connect());
      await this.wallet.ensureChain(chainId);
      const tx = await this.wallet.mint(token, account, BigInt(mintPerCall));
      this.notice.set(`Minting ${formatBaseUnits(BigInt(mintPerCall))} test mUSDT to ${account} (transaction ${tx}).`);
    });
  }

  private async withWallet(action: (token: string, chainId: number, mintPerCall: string) => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    try {
      const payments = this.api.payments() ?? (await this.api.loadShop(), this.api.payments());
      if (!payments?.tokenAddress) {
        this.error.set('Payments are not available in this shop yet.');
        return;
      }
      await action(payments.tokenAddress, payments.chainId, payments.mintPerCall);
    } catch (e) {
      this.error.set(walletMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}

function readStorage(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function writeStorage(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Private mode: the page still shows "confirming" until reload.
  }
}

function removeStorage(key: string): void {
  try {
    localStorage.removeItem(key);
  } catch {
    // Nothing to clean up.
  }
}
