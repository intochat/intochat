# Gmail

## Connections and deployment credentials

The module's ConnectorDefinition declares supported authentication methods and Integration
dependencies. Platform delivers credentials through scoped async Integration handles; credential
values never belong in module options. User authorizations are per-brain Connections holding
SecretRefs. The offered connector catalog is `GET /connectors`, and user connections are managed
at `/brains/{id}/connections`. Provision deployment credentials with
`DigitalBrain:Integrations:{id}:{Field}`; ordinary options stay under `DigitalBrain:Modules:{id}:Options`.

Integrations are what the deployment provisions; Connections are what users authorize.

Existing global/email-keyed Gmail connections must reconnect as named Connections. Both OAuth
and Pub/Sub delivery use the Connection grain key; old Gmail state is not migrated.

Integration `google-oauth` requires `ClientId` and `ClientSecret`, acquired together through
one atomic release. Configure `DigitalBrain:Modules:gmail:Options:PublicOrigin` as the public
HTTPS origin (loopback HTTP is allowed for development). TokenEndpoint and ProfileEndpoint
are ordinary options.

Start named authorization with `POST /brains/{brainId}/connections/{connectionId}/gmail/login`.
The callback URI is `{PublicOrigin}/google/gmail/oauth/callback`. The callback requires an expiring single-use state bound to the authenticated principal, brain
and connection, and rechecks brain membership. Mailbox identity comes from Google's profile
response. Access/refresh SecretRefs are stored under connection-specific vault paths.

Optional Integration `google-pubsub` contains non-secret `Audience` and `ServiceAccountEmail`.
Without it, OAuth connections remain offered and push authentication refuses delivery.
`POST /google/gmail/watch` requires a Google-signed, unexpired bearer token with the configured
audience and verified service-account email. A durable mailbox index routes to connected named
connections; disconnected entries are skipped, and the same mailbox can fan out to authorized
connections in multiple brains. Push delivery does not prune stale index entries.
