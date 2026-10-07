# Supabase

## Connections and deployment credentials

The module's ConnectorDefinition declares supported authentication methods and Integration
dependencies. Platform delivers credentials through scoped async Integration handles; credential
values never belong in module options. User authorizations are per-brain connection neurons holding
sealed credentials. The offered connector catalog is `GET /connectors`, and user connections are managed
at `/brains/{id}/connections`. Provision deployment credentials with
`DigitalBrain:Integrations:{id}:{Field}`; ordinary options stay under `DigitalBrain:Modules:{id}:Options`.

Integrations are what the deployment provisions; Connections are what users authorize.

The Supabase user-project provider resolves the named `supabase` Connection in the caller's
brain; it does not fall back to a deployment credential. The optional deployment backing tier
uses Integration `supabase:ConnectionString` and is selected by ordinary option `UseBackingStore`.
Selecting it requires a valid provisioned credential at startup. Connection-only deployments
can leave that tier unselected. Hosting helpers that select a managed or external backing store
also enable that option.
