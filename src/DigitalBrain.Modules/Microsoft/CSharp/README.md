# CSharp

## Connections and deployment credentials

The sandbox is declared as the `csharp` connector: the external system behind the `ICSharpFile`
neuron, whose public signal is `CSharpFileChanged`. It appears in `GET /connectors`. Its only
credential is the deployment's run-token key, the optional Integration `csharp`, so a user has
nothing to authorize beyond the token each run is issued (the connector names the auth method
`run-token` because Platform requires one). Platform delivers credentials through scoped async
handles authorized by the Integration schema. Credential values never belong in module options.
Provision deployment credentials with `DigitalBrain:Integrations:{id}:{Field}`; ordinary options
stay under `DigitalBrain:Modules:{id}:Options`.

Integrations are what the deployment provisions; Connections are what users authorize.

Integration `csharp` supplies secret `RunTokenKey`. Run-token signing initializes asynchronously
after Platform Integration seeding. Production without that key fails startup; development may
generate a process-local key with a warning that tokens do not survive restart. Deployment
settings contain only non-secret behavior; this migration adds no token TTL/scope redesign.
