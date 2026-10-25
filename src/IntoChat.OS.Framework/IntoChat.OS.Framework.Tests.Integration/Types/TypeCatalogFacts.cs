using IntoChat.OS.Connections;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework.Tests.Integration;

public sealed class TypeCatalogFacts
{
    [Fact]
    public void SecretRefIsNeverCollectedByAForm()
    {
        var secret = TypeCatalog.Get(FieldKind.Secret);
        Assert.Equal(LlmExposure.ReferenceOnly, secret.LlmExposure);
        Assert.Equal(InputWidgetKind.OutOfBand, secret.InputWidget);
        Assert.Equal(SensitivityClass.Credential, secret.Sensitivity);
        Assert.False(secret.Indexable);

        var rejected = Assert.Throws<TypeValidationException>(() => TypeCatalog.Validate("hunter2", FieldKind.Secret));
        Assert.DoesNotContain("hunter2", rejected.Message, StringComparison.Ordinal);
        Assert.Null(rejected.RejectedValue);

        var handle = SecretReferences.For("owner", "salesforce-api-key", "Salesforce API key", isSet: true);
        Assert.Same(handle, TypeCatalog.Validate(handle, FieldKind.Secret));
    }

    [Fact]
    public void DateRejectsTheValueDateAndNamesDate()
    {
        var rejected = Assert.Throws<TypeValidationException>(() => TypeCatalog.Validate("date", FieldKind.Date));

        Assert.Equal(FieldKind.Date, rejected.Kind);
        Assert.Contains("date", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("Date", rejected.Message, StringComparison.Ordinal);

        Assert.Equal(new DateOnly(2026, 9, 23), TypeCatalog.Validate("2026-09-23", FieldKind.Date));
    }

    [Theory]
    [InlineData("2026-10-03T14:20:00+05:30", 330)]
    [InlineData("2026-10-03T14:20:00-07:00", -420)]
    [InlineData("2026-10-03T14:20:00Z", 0)]
    public void DateTimesPreserveTheirOffset(string value, int minutes)
    {
        var result = Assert.IsType<DateTimeOffset>(TypeCatalog.Validate(value, FieldKind.DateTime));
        Assert.Equal(TimeSpan.FromMinutes(minutes), result.Offset);
        Assert.Equal(14, result.Hour);
    }

    [Fact]
    public void DateTimesWithoutAnOffsetAreRejected()
    {
        Assert.Throws<TypeValidationException>(() => TypeCatalog.Validate("2026-10-03T14:20:00", FieldKind.DateTime));
        Assert.Throws<TypeValidationException>(() => TypeCatalog.Validate(new DateTime(2026, 10, 3), FieldKind.DateTime));
    }

    [Fact]
    public void CatalogIsClosedOverExactlyTheV0Kinds()
    {
        var kinds = Enum.GetValues<FieldKind>();
        Assert.Equal(kinds.Length, TypeCatalog.All.Count);
        Assert.Equal(kinds.OrderBy(kind => kind), TypeCatalog.All.Select(type => type.Kind).OrderBy(kind => kind));
        Assert.Equal(kinds.Length, TypeCatalog.All.Select(type => type.Id).Distinct(StringComparer.Ordinal).Count());
    }
}
