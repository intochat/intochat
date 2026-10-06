# CSharp

## Connections and deployment credentials

The module declares deployment infrastructure through its IntegrationSchema and has no
user-connectable ConnectorDefinition. Platform delivers credentials through scoped async
handles authorized by that schema. Credential values never belong in module options, and
CSharp does not appear in `GET /connectors`. Provision deployment credentials with
`DigitalBrain:Integrations:{id}:{Field}`; ordinary options stay under `DigitalBrain:Modules:{id}:Options`.

Integrations are what the deployment provisions; Connections are what users authorize.

Integration `csharp` supplies secret `RunTokenKey`. Run-token signing initializes asynchronously
after Platform Integration seeding. Production without that key fails startup; development may
generate a process-local key with a warning that tokens do not survive restart. Deployment
settings contain only non-secret behavior; this migration adds no token TTL/scope redesign.
