import { Injectable, inject, signal } from '@angular/core';
import {
  ETHEREUM,
  Eip1193Provider,
  chainIdHex,
  encodeBalanceOf,
  encodeMint,
  encodeTransfer,
  hexToBigInt,
  isUserRejection,
} from '../shared/eip1193';

/** Why paying can't go ahead (spec 005 US1 scenarios 2 and 3). */
export class WalletProblem extends Error {}

/**
 * The buyer's MetaMask account (spec 005 FR-002, FR-002a). Reads go through the wallet's own
 * connection; nothing here holds a key or a provider URL.
 */
@Injectable({ providedIn: 'root' })
export class Wallet {
  private readonly ethereum = inject(ETHEREUM);

  /** The connected account (as MetaMask reports it), or null. */
  readonly account = signal<string | null>(null);

  get available(): boolean {
    return this.ethereum !== null;
  }

  private get provider(): Eip1193Provider {
    if (!this.ethereum) {
      throw new WalletProblem('Install MetaMask to pay with mUSDT.');
    }
    return this.ethereum;
  }

  /** Asks MetaMask for an account (`eth_requestAccounts`). */
  async connect(): Promise<string> {
    const accounts = (await this.provider.request({ method: 'eth_requestAccounts' })) as string[];
    if (!Array.isArray(accounts) || accounts.length === 0) {
      throw new WalletProblem('MetaMask gave no account.');
    }
    this.account.set(accounts[0]);
    return accounts[0];
  }

  /** Switches MetaMask to the shop's chain; resolves only once it is on it (US1 scenario 2). */
  async ensureChain(chainId: number): Promise<void> {
    const current = hexToBigInt(await this.provider.request({ method: 'eth_chainId' }));
    if (current === BigInt(chainId)) {
      return;
    }
    await this.provider.request({ method: 'wallet_switchEthereumChain', params: [{ chainId: chainIdHex(chainId) }] });
    const after = hexToBigInt(await this.provider.request({ method: 'eth_chainId' }));
    if (after !== BigInt(chainId)) {
      throw new WalletProblem('Switch MetaMask to Sepolia to pay.');
    }
  }

  async tokenBalance(token: string, account: string): Promise<bigint> {
    return hexToBigInt(
      await this.provider.request({ method: 'eth_call', params: [{ to: token, data: encodeBalanceOf(account) }, 'latest'] }),
    );
  }

  async ethBalance(account: string): Promise<bigint> {
    return hexToBigInt(await this.provider.request({ method: 'eth_getBalance', params: [account, 'latest'] }));
  }

  /** Sends `transfer(to, amount)` on the token from `from`; returns the transaction hash. */
  async transfer(token: string, from: string, to: string, amount: bigint): Promise<string> {
    return (await this.provider.request({
      method: 'eth_sendTransaction',
      params: [{ from, to: token, data: encodeTransfer(to, amount) }],
    })) as string;
  }

  /** Sends `mint(amount)` on the token from `from` (FR-017); returns the transaction hash. */
  async mint(token: string, from: string, amount: bigint): Promise<string> {
    return (await this.provider.request({
      method: 'eth_sendTransaction',
      params: [{ from, to: token, data: encodeMint(amount) }],
    })) as string;
  }
}

/** What to tell the buyer when MetaMask didn't give an account. */
export function walletMessage(error: unknown): string {
  if (error instanceof WalletProblem) {
    return error.message;
  }
  if (isUserRejection(error)) {
    return 'The request was rejected in MetaMask.';
  }
  return 'MetaMask did not answer. Try again.';
}
