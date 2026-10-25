# DigitalBrain.Capabilities.Identity

The `identity@1` capability contract: accounts, brains, principals, membership and grants.

- `IIdentity` (`[Capability("identity", 1)]`): accounts (`ReadAccount`, `OwnerOfBrain`, `EnsureAccount`), brains
  (`CreateBrain`, `EnsureBrain`, `ProvisionOwner`, `BrainsOf`, `KindOf`, `BrainExists`), principals (`IsRegistered`,
  `RegisterPrincipal`, `CompleteRegistration`) and membership (`Membership`, `EnsureMember`, `RemoveMember`,
  `RemovePrincipalMember`).
- `IGrants` (same id): `Authorize(CallRequest)`, `ListGrants`, `GrantAsOwner`, `RevokeAsOwner`.
- `ISignInHandler`: invoked after the session is issued; a failing handler is logged and never fails sign-in.
- No caller parameter anywhere: the implementation reads the runtime-stamped caller (D-15). Session issue takes
  the HTTP request and stays with the host; it is not in this package.
- Records: `Account`, `Member`, `MemberRole`, `BrainEntry`, `BrainKind`, `BrainProvision`, `Grant` (with `Covers`),
  `GrantMode`, `CallRequest`, `CallDecision`, `CallDenial`.

References only `DigitalBrain`; no Orleans, no ASP.NET Core.
