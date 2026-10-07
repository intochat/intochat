# CSharp

## Connections and deployment credentials

The sandbox is declared as the `csharp` connector: the external system behind the `ICSharpFile`
neuron, whose public signal is `CSharpFileChanged`. It is declared, not offered: no connection
neuron serves it, so it does not appear in `GET /connectors` and a connection key naming it
addresses nothing. Its only credential is the deployment's run-token key, the optional Integration
`csharp`; the connector names the auth method `run-token` because Platform requires one. Credential
values never belong in module options. Provision deployment credentials with
`DigitalBrain:Integrations:{id}:{Field}`; ordinary options stay under `DigitalBrain:Modules:{id}:Options`.

Integrations are what the deployment provisions; Connections are what users authorize.

Integration `csharp` supplies secret `RunTokenKey`. Run-token signing initializes asynchronously
after Platform Integration seeding. Production without that key fails startup; development may
generate a process-local key with a warning that tokens do not survive restart. Deployment
settings contain only non-secret behavior; this migration adds no token TTL/scope redesign.
