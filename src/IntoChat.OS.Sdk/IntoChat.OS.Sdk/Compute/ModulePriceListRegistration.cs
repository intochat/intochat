using IntoChat.OS.Contracts.Compute;

namespace IntoChat.OS.Sdk.Compute;

public sealed class ModulePriceListRegistration(string moduleId, Type priceListType, ExternalOperationDescriptor[] operations)
{
    private readonly ExternalOperationDescriptor[] _operations = [.. operations];
    public string ModuleId { get; } = moduleId;
    public Type PriceListType { get; } = priceListType;
    public ExternalOperationDescriptor[] Operations => [.. _operations];
}
