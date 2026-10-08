# Gmail

## Connections and deployment credentials

The module's ConnectorDefinition declares supported authentication methods and Integration
dependencies. Platform delivers credentials through scoped async Integration handles; credential
values never belong in module options. User authorizations are per-brain connection neurons holding
sealed credentials. The offered connector catalog is `GET /connectors`, and user connections are managed
at `/brains/{id}/connections`. Provision deployment credentials with
`DigitalBrain:Integrations:{id}:{Field}`; ordinary options stay under `DigitalBrain:Modules:{id}:Options`.

Integrations are what the deployment provisions; Connections are what users authorize.

Pub/Sub delivery uses the connection grain key.

Integration `google-oauth` requires `ClientId` and `ClientSecret`. The connector declares the
`google-oauth` OAuth method; the Platform owns the browser login, the callback and the token
exchange and refresh. Configure `DigitalBrain:Connections:PublicOrigin` as the public HTTPS origin
(loopback HTTP is allowed for development). ProfileEndpoint is an ordinary option.

Start authorization with `POST /brains/{brainId}/connections/{connectionId}/login` and
`{"connectorId":"gmail","authMethodId":"google-oauth"}`. The callback URI is
`{PublicOrigin}/connections/oauth/gmail/callback`. The Gmail connection neuron keeps the tokens as
sealed state; mailbox identity comes from Google's profile response, and a probe asks the same
endpoint whether the stored token still works.

Optional Integration `google-pubsub` contains non-secret `Audience` and `ServiceAccountEmail`.
Without it, OAuth connections remain offered and push authentication refuses delivery.
`POST /google/gmail/watch` requires a Google-signed, unexpired bearer token with the configured
audience and verified service-account email. A durable mailbox index routes to connected named
connections; disconnected entries are skipped, and the same mailbox can fan out to authorized
connections in multiple brains. Push delivery does not prune stale index entries.
