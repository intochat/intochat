namespace IntoChat.OS.Sdk;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ModulePriceListAttribute(Type priceListType) : Attribute
{
    public Type PriceListType { get; } = priceListType;
}
