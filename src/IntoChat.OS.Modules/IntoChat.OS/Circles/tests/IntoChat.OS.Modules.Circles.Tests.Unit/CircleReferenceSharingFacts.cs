namespace IntoChat.OS.Modules.Circles.Tests;

public sealed class CircleReferenceSharingFacts
{
    [Fact]
    public void CircleBindingStoresReferenceKeysWithoutCopyFlag()
    {
        var binding = new CircleBinding("account-1", "calendar", "primary", "local/connection/google/personal");

        Assert.Equal("account-1", binding.AccountId);
        Assert.Equal("calendar", binding.AppName);
        Assert.Equal("primary", binding.Slot);
        Assert.Equal("local/connection/google/personal", binding.ConnectionKey);

        var properties = typeof(CircleBinding).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.Equal(4, properties.Length);
        Assert.Null(typeof(CircleBinding).GetProperty("Copy"));
    }

    [Fact]
    public void CircleBindingEqualityMatchesExpectedFields()
    {
        var b1 = new CircleBinding("user-a", "tasks", "db", "brain-1/conn/pg/main");
        var b2 = new CircleBinding("user-a", "tasks", "db", "brain-1/conn/pg/main");
        var b3 = new CircleBinding("user-b", "tasks", "db", "brain-1/conn/pg/main");

        Assert.Equal(b1, b2);
        Assert.NotEqual(b1, b3);
    }
}
