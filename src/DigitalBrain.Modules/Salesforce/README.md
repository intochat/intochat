# Salesforce

## Connections and deployment credentials

The module's ConnectorDefinition declares supported authentication methods and Integration
dependencies. Platform delivers credentials through scoped async Integration handles; credential
values never belong in module options. User authorizations are per-brain Connections holding
SecretRefs. The offered connector catalog is `GET /connectors`, and user connections are managed
at `/brains/{id}/connections`. Provision deployment credentials with
`DigitalBrain:Integrations:{id}:{Field}`; ordinary options stay under `DigitalBrain:Modules:{id}:Options`.

Integrations are what the deployment provisions; Connections are what users authorize.

Connector `salesforce` uses `org-oauth` and requires Integration `salesforce` with
`ConsumerKey`/`ConsumerSecret`. Both values come from one atomic release during challenge,
token exchange or refresh. PublicOrigin belongs to ordinary Salesforce options. Authentication
handlers clone options per request before acquiring credentials. Authorization starts at
`/brains/{brainId}/connections/services/salesforce/start`.
