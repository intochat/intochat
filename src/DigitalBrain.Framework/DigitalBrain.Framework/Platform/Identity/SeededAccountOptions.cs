namespace DigitalBrain.Platform.Identity.Configuration;

// Accounts a deployment brings with it: first-party publishers ("intochat", "os") and, in
// development, accounts to switch into. Bound from DigitalBrain:Auth:Accounts:{n}. An entry
// without a password owns a brain but can never sign in.
public sealed class SeededAccountOptions
{
    public const string SectionName = AuthOptions.SectionName;

    public IList<SeededAccount> Accounts { get; } = [];
}

public sealed class SeededAccount
{
    public string Username { get; set; } = "";
    public string? Password { get; set; }
    public string DisplayName { get; set; } = "";
}
