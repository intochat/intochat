# GitHub

## Connections and deployment credentials

The module's ConnectorDefinition declares supported authentication methods and Integration
dependencies. Platform delivers credentials through scoped async Integration handles; credential
values never belong in module options. User authorizations are per-brain Connections holding
SecretRefs. The offered connector catalog is `GET /connectors`, and user connections are managed
at `/brains/{id}/connections`. Provision deployment credentials with
`DigitalBrain:Integrations:{id}:{Field}`; ordinary options stay under `DigitalBrain:Modules:{id}:Options`.

Integrations are what the deployment provisions; Connections are what users authorize.

Integration `github` requires `PrivateKeyPem` and non-secret `AppId`; `ClientSecret` and
`WebhookSecret` are optional secret fields. AppId and signing key are materialized together.
ClientId, Slug, PublicOrigin, PublicWebhookUrl and Repositories belong to ordinary GitHub options.
The repository configuration root is `DigitalBrain:Modules:github:Options:Repositories`.

Aspire accepts an installation-only AppId/private-key configuration. OAuth requires the
ClientId/PublicOrigin pair and projects its client-secret parameter; configuring PublicWebhookUrl
independently projects the webhook-secret parameter. Authentication handlers acquire secrets
asynchronously into per-request option clones. The webhook handler reads its current optional
secret for signature validation. Missing OAuth/webhook credentials do not disable app installation.
