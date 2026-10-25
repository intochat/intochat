using System.Reflection;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Modules.Google.Gmail;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Modules.Google.Gmail.Tests.Unit;

public sealed class GooglePriceListFacts
{
    [Fact]
    public void TheDeclaredPriceListKeepsUnknownProviderPricesDistinctFromZero()
    {
        var declaration = typeof(GmailModule).GetCustomAttribute<ModulePriceListAttribute>();
        Assert.NotNull(declaration);
        var prices = Assert.IsAssignableFrom<IPriceList>(Activator.CreateInstance(declaration.PriceListType));
        foreach (var operation in typeof(GmailModule).GetCustomAttributes<ExternalOperationAttribute>())
        {
            var quote = prices.Quote(CostKind.ExternalProvider, operation.Name, "request");
            Assert.Null(quote.MicrosPerUnit);
            Assert.Equal(PricingStatus.Unpriced, quote.Status);
            Assert.Equal(ExternalChargePolicy.Informational, quote.ChargePolicy);
            Assert.Null(quote.EffectiveFrom);
            Assert.NotNull(quote.Source);
        }
    }
}
