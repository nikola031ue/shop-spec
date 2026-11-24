using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Shop.Api.Storage;

namespace Shop.Api.Auth;

/// <summary>
/// Keeps ASP.NET Core Data-Protection keys in the shop's database (research R11), so all replicas
/// share one key ring. The framework calls this rarely and caches the result, so the blocking
/// calls are acceptable.
/// </summary>
public sealed class StoreXmlRepository(IShopStore store) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements() =>
        store.GetDataProtectionKeysAsync(CancellationToken.None).GetAwaiter().GetResult()
            .Select(k => XElement.Parse(k.Xml))
            .ToList();

    public void StoreElement(XElement element, string friendlyName) =>
        store.StoreDataProtectionKeyAsync(friendlyName, element.ToString(SaveOptions.DisableFormatting),
            CancellationToken.None).GetAwaiter().GetResult();
}
