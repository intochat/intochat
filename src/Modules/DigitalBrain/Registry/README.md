# Registry

Registry exposes contract and capability views through `IRegistry`:

```csharp
await registry.Types();                  // Installed public neuron contracts
await registry.Search("table");          // Search those contract descriptions
await registry.Discover("my app data");  // Current caller's capabilities/resources
```

`Discover` accepts no caller-selected account or brain. It requires trusted SDK caller
context and refuses app callers. Module-owned `IRegistryResourceProvider` implementations
read authoritative indexes using that context: installed-app state for app operations and
Postgres ownership indexes for app tables. Registry persists no second resource catalog.

The query ranks the complete provider catalog; spelling does not remove capabilities.
Providers return explicit agent tool bindings and resource metadata, with partial errors
when a resource cannot be described. Observed activation history is not authorization or
proof that a resource exists. Actual invocations continue enforcing module/SDK permissions.

The Assistant adapts discovery results into `AgentToolOffer`. The generic AI runner resolves
those bindings through registered factories and dynamic sources, without database-specific
regexes. Reflected contract methods do not automatically become executable model tools.

Resource browsing is paged and bounded by a per-provider timeout. Discovery is derived; no registry state is persisted.
