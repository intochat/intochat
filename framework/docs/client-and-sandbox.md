# Client and Sandbox

How code outside a brain calls into it, and how the host confines code it compiles at runtime.

## Calling a neuron

Inside a brain, a neuron calls another neuron through a typed interface: `Get<T>(id)`.
The proxy behind it is `NeuronProxy`. Every call becomes one `RuntimeInvocation` (contract, key, method, arguments) handed to an `IInvocationSink`.
TestOS and the host runtime each implement the sink, so both see the same encoded call.
The runtime stamps the caller. The proxy never does.

## The HTTP edge

`DigitalBrain.Runtime.Http` is the HTTP edge on ASP.NET Core. It has no Orleans reference.

- `BrainRouteGroups.Brain(endpoints)` maps `/brains/{brainId}`. It checks the brain id and that the caller is a member of the brain.
- `NeuronsService.Map(endpoints)` maps `POST /brains/{brainId}/neurons/call`. The body is a `NeuronCallRequest`: `Contract`, `Key`, `Method`, `Arguments`.
  `NeuronSurface` turns it into a `RuntimeInvocation` and hands it to the host's `IRuntimeInvocationDispatcher`.
- `JournalTailEndpoints.Map(endpoints)` maps `GET /brains/{brainId}/journal/tail`: a server-sent event stream of the brain journal.
- A module adds its own routes with `IModuleHttpSurface`. The host maps them with `MapModuleHttpSurfaces()`. Every route needs an authenticated caller unless it opts out.
- `ISessionIssuer` writes and clears the session cookie. `SessionPrincipal.Create` builds the cookie principal.

See `src/DigitalBrain.Runtime.Http/README.md`.

## Sandbox for runtime-compiled units

Synapse units arrive as source. `DigitalBrain.Runtime.Synapses` compiles and loads them inside the host process, under these limits:

- `SynapseCompiler` compiles the source with the DigitalBrain analyzers.
- `UnitLoader` loads each build into its own collectible load context.
- Before loading, every referenced type and member is checked against `AllowedApis`. No network, disk, processes, reflection or native code.
- A unit reaches the outside only through a `[Slot]` parameter: a `[Capability]` interface the installer connects.
- Secrets stay behind capabilities. A unit never holds them.

The allow-list is described in [Neurons and Synapses](neurons-and-synapses.md).

## Declaring an out-of-process sandbox

A module that needs isolated code execution declares it in its manifest with `Need.Sandbox` or `Need.Container(name, image)`.
The host supplies the resource. See [Modules](modules.md).
