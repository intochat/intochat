using DigitalBrain.Neurons;
using IntoChat.OS.Authority.Identity.Rules;

namespace IntoChat.OS.Authority.Identity.Contacts;

[Neuron(IContactsNeuron.TypeName)]
public sealed class ContactsNeuron : Neuron<ContactsState>, IContactsNeuron
{
    public async Task Add(string list, ContactEntry entry)
    {
        list ??= "";
        await Check("Add", new ContactListNameIsValid(list));

        ArgumentNullException.ThrowIfNull(entry);
        await Check("Add", new ContactPrincipalIsNotEmpty(entry.Principal));

        var lists = (State.Lists ?? []).ToList();
        var index = lists.FindIndex(l => string.Equals(l.Name, list, StringComparison.Ordinal));
        if (index < 0)
        {
            lists.Add(new ContactList { Name = list, Members = [entry] });
        }
        else
        {
            var currentList = lists[index];
            var members = (currentList.Members ?? []).Where(m => !string.Equals(m.Principal, entry.Principal, StringComparison.Ordinal)).ToList();
            members.Add(entry);
            lists[index] = currentList with { Members = [.. members] };
        }

        var nextVersion = State.Version + 1;
        var nextState = new ContactsState
        {
            Lists = [.. lists],
            Version = nextVersion,
        };
        await Save(nextState, new ContactsChanged(nextVersion, list));
    }

    public async Task Remove(string list, string principal)
    {
        if (string.IsNullOrWhiteSpace(list) || string.IsNullOrWhiteSpace(principal))
        {
            return;
        }

        var lists = (State.Lists ?? []).ToList();
        var index = lists.FindIndex(l => string.Equals(l.Name, list, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }

        var currentList = lists[index];
        var members = (currentList.Members ?? []).Where(m => !string.Equals(m.Principal, principal, StringComparison.Ordinal)).ToArray();
        if (members.Length == (currentList.Members ?? []).Length)
        {
            return;
        }

        lists[index] = currentList with { Members = members };
        var nextVersion = State.Version + 1;
        var nextState = new ContactsState
        {
            Lists = [.. lists],
            Version = nextVersion,
        };
        await Save(nextState, new ContactsChanged(nextVersion, list));
    }

    public Task<ContactList[]> Lists() => Task.FromResult(State.Lists ?? []);

    public Task<bool> IsMember(string list, string principal)
    {
        var found = (State.Lists ?? []).FirstOrDefault(l => string.Equals(l.Name, list, StringComparison.Ordinal));
        return Task.FromResult(found?.Members?.Any(m => string.Equals(m.Principal, principal, StringComparison.Ordinal)) ?? false);
    }

    public Task<string[]> ListsContaining(string principal)
    {
        var listNames = (State.Lists ?? [])
            .Where(l => l.Members?.Any(m => string.Equals(m.Principal, principal, StringComparison.Ordinal)) == true)
            .Select(l => l.Name)
            .ToArray();
        return Task.FromResult(listNames);
    }

    public Task<long> GetVersion() => Task.FromResult(State.Version);
}
