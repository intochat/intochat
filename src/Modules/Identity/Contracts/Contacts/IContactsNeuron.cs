using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Identity.Contracts.Contacts;

[NeuronAlias(TypeName), DefaultNeuronType(TypeName)]
public interface IContactsNeuron
{
    const string TypeName = "contacts.default";
    const string DefaultKey = "contacts/default";
    static string Key(string brain) => $"{brain}/contacts/default";
    static string Userset(string list) => $"contacts/{list}#member";   // the principal string
    Task Add(string list, ContactEntry entry);        // creates the list if missing; replaces an entry with the same Principal
    Task Remove(string list, string principal);
    Task<ContactList[]> Lists();
    Task<bool> IsMember(string list, string principal);
    Task<string[]> ListsContaining(string principal); // list names
    Task<long> GetVersion();
}

[GenerateSerializer, Alias("identity.contact-entry")]
public sealed record ContactEntry
{
    [Id(0)] public string Principal { get; init; } = "";
    [Id(1)] public string Handle { get; init; } = "";
    [Id(2)] public string DisplayName { get; init; } = "";
    [Id(3)] public DateTimeOffset AddedAt { get; init; }
}

[GenerateSerializer, Alias("identity.contact-list")]
public sealed record ContactList
{
    [Id(0)] public string Name { get; init; } = "";
    [Id(1)] public ContactEntry[] Members { get; init; } = [];
}

[GenerateSerializer, Alias("identity.contacts-state")]
public sealed record ContactsState
{
    [Id(0)] public ContactList[] Lists { get; init; } = [];
    [Id(1)] public long Version { get; init; }
}

[Signal("identity.contacts-changed")]
public sealed record ContactsChanged(long Version, string List) : Signal;
