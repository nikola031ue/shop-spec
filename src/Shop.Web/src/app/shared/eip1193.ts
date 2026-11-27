import { InjectionToken } from '@angular/core';

/**
 * The browser wallet (MetaMask) through EIP-1193, without a wallet library (spec 005
 * contracts/shop-api.md "Angular"): three fixed ABI calls encoded by hand.
 */
export interface Eip1193Provider {
  request(args: { method: string; params?: unknown[] }): Promise<unknown>;
}

/** The injected provider (`window.ethereum`), or null without MetaMask. Tests provide a fake. */
export const ETHEREUM = new InjectionToken<Eip1193Provider | null>('ETHEREUM', {
  providedIn: 'root',
  factory: () => (globalThis as { ethereum?: Eip1193Provider }).ethereum ?? null,
});

/** contracts/musdt.md selectors. */
export const SELECTOR = {
  transfer: 'a9059cbb',
  balanceOf: '70a08231',
  mint: 'a0712d68',
} as const;

const ADDRESS = /^0x[0-9a-fA-F]{40}$/;

function word(hex: string): string {
  return hex.padStart(64, '0');
}

function addressWord(address: string): string {
  if (!ADDRESS.test(address)) {
    throw new Error('Not an Ethereum address.');
  }
  return word(address.slice(2).toLowerCase());
}

function uintWord(value: bigint): string {
  if (value < 0n) {
    throw new Error('Amounts are never negative.');
  }
  return word(value.toString(16));
}

/** `transfer(address to, uint256 amount)` call data. */
export function encodeTransfer(to: string, amount: bigint): string {
  return '0x' + SELECTOR.transfer + addressWord(to) + uintWord(amount);
}

/** `mint(uint256 amount)` call data (mints to the sender, at most 1,000 mUSDT per call). */
export function encodeMint(amount: bigint): string {
  return '0x' + SELECTOR.mint + uintWord(amount);
}

/** `balanceOf(address)` call data. */
export function encodeBalanceOf(account: string): string {
  return '0x' + SELECTOR.balanceOf + addressWord(account);
}

/** A JSON-RPC quantity or 32-byte word ("0x…") as a bigint; "0x" alone is zero. */
export function hexToBigInt(hex: unknown): bigint {
  if (typeof hex !== 'string' || !/^0x[0-9a-fA-F]*$/.test(hex)) {
    throw new Error('Unexpected answer from the wallet.');
  }
  return hex === '0x' ? 0n : BigInt(hex);
}

export function chainIdHex(chainId: number): string {
  return '0x' + chainId.toString(16);
}

/** Addresses compare case-insensitively (EIP-55 only changes letter case). */
export function sameAddress(a: string | null | undefined, b: string | null | undefined): boolean {
  return !!a && !!b && a.toLowerCase() === b.toLowerCase();
}

/** 6-decimal base units → "11.00" (whole cents; mUSDT prices have at most 2 decimals). */
export function formatBaseUnits(units: bigint): string {
  const cents = units / 10_000n;
  const whole = cents / 100n;
  const fraction = (cents % 100n).toString().padStart(2, '0');
  return `${whole}.${fraction}`;
}

/** EIP-1193 "user rejected the request". */
export function isUserRejection(error: unknown): boolean {
  return (error as { code?: unknown } | null)?.code === 4001;
}
