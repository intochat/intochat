# CSharp

## Connections and deployment credentials

The module's ConnectorDefinition declares supported authentication methods and Integration
dependencies. Platform delivers credentials through scoped async Integration handles; credential
values never belong in module options. User authorizations are per-brain Connections holding
SecretRefs. The offered connector catalog is `GET /connectors`, and user connections are managed
at `/brains/{id}/connections`. Provision deployment credentials with
`DigitalBrain:Integrations:{id}:{Field}`; ordinary options stay under `DigitalBrain:Modules:{id}:Options`.

Integrations are what the deployment provisions; Connections are what users authorize.

Integration `csharp` supplies secret `RunTokenKey`. Run-token signing initializes asynchronously
after Platform Integration seeding. Production without that key fails startup; development may
generate a process-local key with a warning that tokens do not survive restart. Deployment
settings contain only non-secret behavior; this migration adds no token TTL/scope redesign.
