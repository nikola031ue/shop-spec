using System.Security.Cryptography;
using System.Text;
using Shop.Api.Domain;

namespace Shop.Api.Tests.Unit;

/// <summary>EIP-55 and Keccak-256, copied with ShopHub's implementation (spec 004) and its tests.</summary>
public sealed class Eip55Tests
{
    private const string Valid = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";

    [Theory]
    [InlineData(Valid)]
    [InlineData("0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359")]
    [InlineData("0xdF8d5dEb736EBC79f7e99C4c90975d4b8f8d80E2")] // the mUSDT token on Sepolia
    public void ChecksummedAddressesPassAndStayAsTheyAre(string address)
    {
        Assert.Equal(address, Eip55.Normalize(address, out var problem));
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("0xfb6916095ca1df60bb79ce92ce3ea74c37c5d359", "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359")]
    [InlineData("0xFB6916095CA1DF60BB79CE92CE3EA74C37C5D359", "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359")]
    [InlineData("  0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed ", Valid)]
    public void UniformCaseHasNoChecksumAndIsStoredChecksummed(string input, string stored)
    {
        Assert.Equal(stored, Eip55.Normalize(input, out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void MixedCaseWithAWrongLetterFailsTheChecksum()
    {
        Assert.Null(Eip55.Normalize("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAEd", out var problem));
        Assert.Equal(Eip55.ChecksumProblem, problem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed")]
    [InlineData("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAeZ")]
    [InlineData("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed00")]
    [InlineData("0X5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed")]
    public void AnythingElseIsAFormatProblem(string? input)
    {
        Assert.Null(Eip55.Normalize(input, out var problem));
        Assert.Equal(Eip55.FormatProblem, problem);
    }

    [Fact]
    public void KeccakMatchesKnownDigests()
    {
        Assert.Equal("c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470",
            Convert.ToHexStringLower(Keccak.Hash256([])));
        Assert.Equal("4e03657aea45a94fc7d47ba826c8d667c0d1e6e33a64a036ec44f58fa12d6c45",
            Convert.ToHexStringLower(Keccak.Hash256("abc"u8)));
        // The ERC20 Transfer event topic the operator filters on (contracts/musdt.md).
        Assert.Equal("ddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef",
            Convert.ToHexStringLower(Keccak.Hash256("Transfer(address,address,uint256)"u8)));
    }

    [Fact]
    public void SpongeMatchesPlatformSha3AcrossBlockSizes()
    {
        Assert.SkipUnless(SHA3_256.IsSupported, "SHA3-256 not available on this platform");
        foreach (var length in new[] { 0, 1, 135, 136, 137, 271, 272, 500 })
        {
            var data = Encoding.ASCII.GetBytes(new string('a', length));
            Assert.Equal(SHA3_256.HashData(data), Keccak.Sponge(data, 0x06));
        }
    }
}
