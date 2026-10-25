# DigitalBrain.Capabilities.Secrets

The `secrets@1` capability contract: the named secrets of a brain.

- `ISecrets` (`[Capability("secrets", 1)]`): `Set(name, label, value)` returns a `SecretRef`, `Remove(name)`,
  `Resolve(SecretRef)` returns the value.
- No caller parameter: the implementation reads the caller the runtime stamped on the call (D-15).
- `SecretRef(Reference, Label, Status)` never holds the value; `ToString()` prints `secret <name> (set|unset)`.
- Errors derive from `SecretException` with stable codes `not-found` (`SecretNotFound`) and `denied` (`SecretDenied`);
  messages carry the name only.

References only `DigitalBrain`; no Orleans, no ASP.NET Core.
