import {
  chainIdHex,
  encodeBalanceOf,
  encodeMint,
  encodeTransfer,
  formatBaseUnits,
  hexToBigInt,
  isUserRejection,
  sameAddress,
} from './eip1193';

const to = '0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed';

describe('EIP-1193 helpers (spec 005 contracts/shop-api.md, musdt.md)', () => {
  it('encodes transfer(address,uint256) with the standard selector and 32-byte words', () => {
    expect(encodeTransfer(to, 11_000_000n)).toBe(
      '0xa9059cbb' +
        '0000000000000000000000005aaeb6053f3e94c9b9a09f33669435e7ef1beaed' +
        '0000000000000000000000000000000000000000000000000000000000a7d8c0',
    );
  });

  it('encodes mint(uint256) and balanceOf(address)', () => {
    expect(encodeMint(1_000_000_000n)).toBe('0xa0712d68' + '000000000000000000000000000000000000000000000000000000003b9aca00');
    expect(encodeBalanceOf(to)).toBe('0x70a08231' + '0000000000000000000000005aaeb6053f3e94c9b9a09f33669435e7ef1beaed');
  });

  it('refuses bad addresses and negative amounts', () => {
    expect(() => encodeTransfer('0x123', 1n)).toThrow();
    expect(() => encodeTransfer(to, -1n)).toThrow();
  });

  it('reads JSON-RPC quantities and words', () => {
    expect(hexToBigInt('0xaa36a7')).toBe(11155111n);
    expect(hexToBigInt('0x')).toBe(0n);
    expect(hexToBigInt('0x' + '0'.repeat(63) + '1')).toBe(1n);
    expect(() => hexToBigInt('12')).toThrow();
    expect(() => hexToBigInt(undefined)).toThrow();
    expect(chainIdHex(11155111)).toBe('0xaa36a7');
  });

  it('formats base units as mUSDT with cents, compares addresses ignoring case, spots rejections', () => {
    expect(formatBaseUnits(11_000_000n)).toBe('11.00');
    expect(formatBaseUnits(10_999_999n)).toBe('10.99');
    expect(formatBaseUnits(5n)).toBe('0.00');
    expect(sameAddress(to, to.toLowerCase())).toBe(true);
    expect(sameAddress(to, null)).toBe(false);
    expect(isUserRejection({ code: 4001 })).toBe(true);
    expect(isUserRejection(new Error('x'))).toBe(false);
  });
});
