using System.Security.Cryptography;
using Shop.Api.Domain;

namespace Shop.Api.Tests;

/// <summary>Random Ethereum accounts for tests (EIP-55 form), so orders never share a paying account by accident.</summary>
public static class TestAccounts
{
    public static string New() =>
        Eip55.Normalize("0x" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20)), out _)!;

    /// <summary>The mUSDT token address used by the test environment (Sepolia deployment).</summary>
    public const string Token = "0xdF8d5dEb736EBC79f7e99C4c90975d4b8f8d80E2";
}
