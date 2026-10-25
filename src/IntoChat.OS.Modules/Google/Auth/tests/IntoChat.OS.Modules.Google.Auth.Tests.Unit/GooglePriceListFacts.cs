using System.Reflection;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Google.Drive;
using IntoChat.OS.Modules.Google.Gmail;

namespace IntoChat.OS.Modules.Google.Auth.Tests.Unit;

public sealed class GooglePriceListFacts
{
    [Theory]
    [InlineData(typeof(GoogleAuthModule))]
    [InlineData(typeof(GmailModule))]
    [InlineData(typeof(DriveModule))]
    public void TheDeclaredPriceListKeepsUnknownProviderPricesDistinctFromZero(Type module)
    {
        var declaration = module.GetCustomAttribute<ModulePriceListAttribute>();
        Assert.NotNull(declaration);
        Assert.Equal(typeof(GooglePriceList), declaration.PriceListType);
        var prices = Assert.IsAssignableFrom<IPriceList>(Activator.CreateInstance(declaration.PriceListType));
        Assert.NotEmpty(module.GetCustomAttributes<ExternalOperationAttribute>());
        foreach (var operation in module.GetCustomAttributes<ExternalOperationAttribute>())
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
