using System.Reflection;
using System.Text.Json;
using IntoChat.OS.Contracts.Compute;

namespace IntoChat.OS.Framework.Tests.Unit;

public sealed class ComputeContractFacts
{
    [Fact]
    public void AuthoringContractsDoNotRequireTheRuntimeAssembly()
    {
        Type[] types = [typeof(ICostMeter), typeof(IPriceList), typeof(Cost), typeof(Funding), typeof(CostSubject)];
        Assert.All(types, type => Assert.Equal("IntoChat.OS.Contracts.Compute", type.Assembly.GetName().Name));
        Assert.DoesNotContain(typeof(ICostMeter).Assembly.GetReferencedAssemblies(),
            reference => reference.Name is "IntoChat.OS.Framework" or "IntoChat.OS.Authority" or "IntoChat.OS.Client");
    }

    [Fact]
    public void CostRetainsItsHistoricalWireIdentity()
    {
        Assert.Equal(0, (int)CostKind.Model);
        Assert.Equal(1, (int)CostKind.Script);
        Assert.Equal(2, (int)CostKind.Http);
        Assert.Equal(3, (int)CostKind.Storage);
        Assert.Equal(4, (int)CostKind.Operation);
        Assert.Equal(5, (int)CostKind.ExternalProvider);
        var alias = typeof(Cost).CustomAttributes.Single(attribute => attribute.AttributeType.Name == "AliasAttribute");
        Assert.Equal("compute.cost", alias.ConstructorArguments[0].Value);
        string[] historical = ["TraceId", "CostId", "Kind", "Item", "Quantity", "Unit", "Micros", "Synapse", "App", "Neuron", "At"];
        for (var id = 0; id < historical.Length; id++)
        {
            var attribute = typeof(Cost).GetProperty(historical[id])!.CustomAttributes.Single(value => value.AttributeType.Name == "IdAttribute");
            Assert.Equal((uint)id, attribute.ConstructorArguments[0].Value);
        }
    }

    [Fact]
    public void OldCostPayloadsKeepUnknownMetadata()
    {
        const string json = """{"TraceId":"trace","CostId":"old","Kind":0,"Item":"model","Quantity":1,"Unit":"token","Micros":0}""";
        var cost = JsonSerializer.Deserialize<Cost>(json)!;
        Assert.Null(cost.PricingStatus);
        Assert.Null(cost.ListPriceMicros);
        Assert.Null(cost.EstimatedListPriceMicros);
        Assert.Null(cost.FundingKind);
        Assert.Null(cost.AttemptState);
    }

    [Fact]
    public void LegacyQuotesDistinguishKnownZeroFromUnpricedWithoutInventingAnExternalTariff()
    {
        IPriceList priceList = new LegacyPriceList();
        var free = priceList.Quote(CostKind.Model, "zero", "token");
        var unknown = priceList.Quote(CostKind.Model, "unknown", "token");
        Assert.Equal(PricingStatus.Known, free.Status);
        Assert.Equal(0, free.MicrosPerUnit);
        Assert.Equal(PricingStatus.Unpriced, unknown.Status);
        Assert.Null(unknown.MicrosPerUnit);
        Assert.Equal(ExternalChargePolicy.Unspecified, free.ChargePolicy);
    }

    private sealed class LegacyPriceList : IPriceList
    {
        public long? MicrosPerUnit(CostKind kind, string item) => item == "zero" ? 0 : null;
    }
}
