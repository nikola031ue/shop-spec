using Shop.Api.Domain;
using Shop.Api.Storage.Postgres;

namespace Shop.Api.Tests.Unit;

public sealed class DomainTests
{
    [Theory]
    [InlineData("15", 1500)]
    [InlineData("15.5", 1550)]
    [InlineData("15.05", 1505)]
    [InlineData("0.01", 1)]
    [InlineData("999999999.99", 99_999_999_999)]
    public void PriceParsesToCents(string text, long cents)
    {
        Assert.True(Money.TryParsePrice(text, out var parsed));
        Assert.Equal(cents, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("1.234")]
    [InlineData("-1")]
    [InlineData("1,50")]
    [InlineData(" 1")]
    [InlineData("1e3")]
    [InlineData("1000000000")]
    public void InvalidPricesAreRejected(string? text) => Assert.False(Money.TryParsePrice(text, out _));

    [Fact]
    public void ItemIsTrimmedAndKeyedCaseInsensitively()
    {
        var (draft, errors) = ItemRules.Validate("  Blue Shirt ", 3, "15.00");

        Assert.Empty(errors);
        Assert.Equal(new ItemDraft("Blue Shirt", 3, 1500), draft);
        Assert.Equal("blue shirt", draft!.NameKey);
    }

    [Fact]
    public void ItemErrorsAreKeyedByField()
    {
        var (draft, errors) = ItemRules.Validate("   ", -1, "abc");

        Assert.Null(draft);
        Assert.Equal(["name", "price", "stock"], errors.Keys.Order());
    }

    [Fact]
    public void ItemNameIsAtMost100Characters()
    {
        Assert.Empty(ItemRules.Validate(new string('a', 100), 0, "1").Errors);
        Assert.Contains("name", ItemRules.Validate(new string('a', 101), 0, "1").Errors.Keys);
    }

    [Fact]
    public void DuplicateOrderLinesAreMerged()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        var (lines, errors) = OrderRules.Normalize([new(a, 1, 100), new(b, 2, 200), new(a, 3, 100)]);

        Assert.Empty(errors);
        Assert.Equal([new(a, 4, 100), new(b, 2, 200)], lines!);
    }

    [Fact]
    public void OrderLineRules()
    {
        var a = Guid.CreateVersion7();

        Assert.Contains("lines", OrderRules.Normalize([]).Errors.Keys);
        Assert.Contains("lines", OrderRules.Normalize(null).Errors.Keys);
        Assert.Contains("lines", OrderRules.Normalize(Enumerable.Range(0, 51).Select(_ => new OrderRequestLine(Guid.CreateVersion7(), 1, 1)).ToList()).Errors.Keys);
        Assert.Empty(OrderRules.Normalize(Enumerable.Range(0, 50).Select(_ => new OrderRequestLine(Guid.CreateVersion7(), 1, 1)).ToList()).Errors);
        Assert.Contains("lines.quantity", OrderRules.Normalize([new(a, 0, 100)]).Errors.Keys);
        Assert.Contains("lines.unitPriceCents", OrderRules.Normalize([new(a, 1, 0)]).Errors.Keys);
        Assert.Contains("lines.unitPriceCents", OrderRules.Normalize([new(a, 1, 100), new(a, 1, 200)]).Errors.Keys);
    }

    [Fact]
    public void QuantityPerLineIsCapped()
    {
        var a = Guid.CreateVersion7();

        Assert.Empty(OrderRules.Normalize([new(a, OrderRules.MaxQuantityPerLine, 100)]).Errors);
        Assert.Contains("lines.quantity", OrderRules.Normalize([new(a, OrderRules.MaxQuantityPerLine + 1, 100)]).Errors.Keys);
        Assert.Contains("lines.quantity", OrderRules.Normalize([new(a, int.MaxValue, 100)]).Errors.Keys);

        // Duplicates are merged first, so splitting a line doesn't get around the cap.
        Assert.Contains("lines.quantity", OrderRules.Normalize([new(a, 600, 100), new(a, 401, 100)]).Errors.Keys);
        Assert.Equal(1000, OrderRules.Normalize([new(a, 600, 100), new(a, 400, 100)]).Lines!.Single().Quantity);
    }

    [Fact]
    public void LargestAllowedOrderTotalStaysExact()
    {
        var largest = Enumerable.Range(0, OrderRules.MaxLines)
            .Select(_ => new OrderRequestLine(Guid.CreateVersion7(), OrderRules.MaxQuantityPerLine, Money.MaxPriceCents))
            .ToList();

        Assert.Empty(OrderRules.Normalize(largest).Errors);
        Assert.True((Int128)OrderRules.MaxLines * OrderRules.MaxQuantityPerLine * Money.MaxPriceCents <= OrderRules.MaxTotalCents);
    }

    [Fact]
    public void ReferencesAreTenCrockfordCharacters()
    {
        var refs = Enumerable.Range(0, 1000).Select(_ => OrderReference.New()).ToList();

        Assert.All(refs, r =>
        {
            Assert.Equal(10, r.Length);
            Assert.All(r, c => Assert.Contains(c, OrderReference.Alphabet));
        });
        Assert.Equal(refs.Count, refs.Distinct().Count());
    }

    [Fact]
    public void LockoutLocksOnFifthFailureWithinWindow()
    {
        var policy = LockoutPolicy.Default;
        var t0 = DateTimeOffset.UnixEpoch.AddDays(1);
        var admin = new AdminRecord("h", true, null, 0, null, null);

        for (var i = 1; i <= 4; i++)
        {
            admin = policy.ApplyFailure(admin, t0.AddMinutes(i));
            Assert.Equal(i, admin.FailedCount);
            Assert.False(policy.IsLocked(admin, t0.AddMinutes(i), out _));
        }

        admin = policy.ApplyFailure(admin, t0.AddMinutes(5));

        Assert.True(policy.IsLocked(admin, t0.AddMinutes(5), out var retry));
        Assert.Equal(TimeSpan.FromMinutes(15), retry);
        Assert.Equal(0, admin.FailedCount);
        Assert.Null(admin.FailedWindowStart);
        Assert.False(policy.IsLocked(admin, t0.AddMinutes(20), out _));
    }

    [Fact]
    public void LockoutWindowStartsAtFirstFailure()
    {
        var policy = LockoutPolicy.Default;
        var t0 = DateTimeOffset.UnixEpoch.AddDays(1);
        var admin = new AdminRecord("h", true, null, 0, null, null);

        admin = policy.ApplyFailure(admin, t0);
        admin = policy.ApplyFailure(admin, t0.AddMinutes(14));
        Assert.Equal(2, admin.FailedCount);

        admin = policy.ApplyFailure(admin, t0.AddMinutes(15));

        Assert.Equal(1, admin.FailedCount);
        Assert.Equal(t0.AddMinutes(15), admin.FailedWindowStart);
    }

    [Theory]
    [InlineData("50%", @"50\%")]
    [InlineData("a_b", @"a\_b")]
    [InlineData(@"c:\x", @"c:\\x")]
    public void LikePatternsAreEscaped(string input, string escaped) =>
        Assert.Equal(escaped, PostgresShopStore.EscapeLike(input));

    [Theory]
    [InlineData("connect to postgresql://app:hunter2@db:5432/app failed", "connect to postgresql://***@db:5432/app failed")]
    [InlineData("Host=db;Password=hunter2;Database=app", "Host=db;Password=***;Database=app")]
    [InlineData("redis,password=hunter2,abortConnect=False", "redis,password=***,abortConnect=False")]
    public void RedactionRemovesCredentials(string input, string expected) =>
        Assert.Equal(expected, Redaction.Redact(input));
}
