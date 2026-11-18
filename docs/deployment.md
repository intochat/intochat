# Deployment & Pulumi Infrastructure-as-Code

IntoChat OS supports **universal, zero-config-file deployment** to any Azure environment (`--env <name>`, such as `test`, `dev`, or `stage`) using either **Pulumi Infrastructure-as-Code** (`aspire do pulumi`) or direct **Azure Resource Manager (ARM)** provisioning (`install --env <name>`).

No static `deploy/<env>.json` files are required: `InstallConfig.Universal(env, ...)` derives every Azure resource name deterministically from `(prefix, env)` and synthesizes Key Vault secrets and Container App environment variables directly from the composed `OsModule` manifests and `IntegrationSchema` definitions.

---

## 1. Universal Resource Naming (`--env <name>`)

For any environment `<env>` (default `test`) and prefix `<prefix>` (default `intochat`), the stack provisions:

| Resource | Derived Name (`--env test`) | Purpose |
| :--- | :--- | :--- |
| **Resource Group** | `rg-intochat-test` | Holds all foundation and container resources for the environment. |
| **Managed Identity** | `intochat-test-id` | User-assigned identity granted Key Vault, Storage Blob/Table, and ACR pull roles. |
| **Storage Account** | `intochattestst` | Orleans clustering tables, reminders, `grainstate-v1` blob container, `brainlease-v1`, and `brainlog` Azure Files share. |
| **Key Vault** | `intochat-test-kv` | Holds `master-key`, `db-connection`, and every module/integration secret (`{module}-{secret}`). |
| **Container Registry** | `acrintochattest` | Holds the `os` image (and `cloud` image when deploying multi-tenant Cloud). |
| **PostgreSQL Flexible Server** | `intochat-test-pg` | Burstable `Standard_B1ms` (32 GB) relational store wired to `db-connection`. |
| **Log Analytics Workspace** | `intochat-test-log` | Container Apps console and OpenTelemetry log sink. |
| **Container Apps Environment** | `intochat-test-env` | Shared network, mounted `brainlog` file share, and Orleans cluster boundary (`Orleans__ClusterId=intochat-test`). |
| **OS Container App** | `intochat-test-os` | Runs `acrintochattest.azurecr.io/os:latest`: Native AOT Kernel (`/app/silo`), Flutter Web UI + 15 ArtCraft studios (`/app/wwwroot`), and DevKit Roslyn Sandbox (`/app/unithost`). |

> **Note on Multi-Tenant Cloud**: When synthesized from the external Cloud host repository, the same foundation additionally provisions a second Container App (`intochat-<env>-cloud`) in the same `intochat-<env>-env` and Orleans cluster (`Orleans__ClusterId=intochat-<env>`), plus the Key Vault secret references required by the multi-tenant modules.

---

## 2. How Sandboxes Run Locally vs. in Azure Cloud

There are **zero Azure Container Apps Session Pools (`Microsoft.App/sessionPools`)** in the deployment plan (eliminating the dedicated E16 node cost of ~€67/day). Instead, `HttpSandboxRuntimeBridge` supports three execution modes for `IntoChat.OS.Sandbox`:

1. **Local Development with Aspire (`local` topology)**:
   - `SandboxModuleHosting` runs `IntoChat.OS.Sandbox` inside a hardened local Docker container (`--read-only`, `--cap-drop=ALL`, `--security-opt=no-new-privileges:true`, `--memory=512m`, `--cpus=1.0`, `--tmpfs /tmp:rw,noexec,nosuid,size=64m`).
   - `SandboxKernelProjection` sets `IntoChatOS__Modules__sandbox__Options__Url` on the Kernel to point to the `sandbox` container's HTTP endpoint (`/apps/compile`, `/apps/deploy`, `/apps/dispatch`, `/apps/unload`) authenticated with `X-Sandbox-Enroll-Key`.
2. **Cloud & Desktop Default (`--unit-host` Child Process)**:
   - Inside the unified `os` container image (`src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/Dockerfile`) and the Windows desktop bundle (`os/` + `unithost/`), `IntoChat.OS.Sandbox` is pre-published at `/app/unithost` (`unithost/IntoChat.OS.Sandbox`).
   - When `SandboxOptions.Url` is unset, `HttpSandboxRuntimeBridge` spawns `/app/unithost/IntoChat.OS.Sandbox --unit-host` as an environment-scrubbed (`ChildEnvironmentPolicy`), job-object-governed child process communicating over NDJSON stdin/stdout pipes with zero network sockets and zero extra container cost.
3. **Optional Separate Sandbox Container App in Azure**:
   - Because `IntoChat.OS.Sandbox` has its own `src/IntoChat.OS.Sandbox/Dockerfile` and HTTP API, a deployment can also run `intochat-<env>-sandbox` as an internal-ingress Container App in `intochat-<env>-env` and set `IntoChatOS__Modules__sandbox__Options__Url=http://intochat-<env>-sandbox` if physical container-level isolation is desired in the cloud.

---

## 3. Commands (`aspire do pulumi` & CLI)

From `src/IntoChat.OS.AppHost` (or the repository root via `dotnet run --project src/IntoChat.OS.AppHost/IntoChat.OS.AppHost.csproj --`):

```powershell
# 1. Synthesize artifacts/pulumi/<env>/Pulumi.yaml + Pulumi.<env>.yaml + import.<env>.json (no Azure call)
dotnet run --project src/IntoChat.OS.AppHost/IntoChat.OS.AppHost.csproj -- pulumi --env test --synth-only

# 2. Preview infrastructure changes via Aspire 17.1 pipeline step
aspire do pulumi-preview

# 3. Refresh Pulumi state against live Azure resources
aspire do pulumi-refresh

# 4. Deploy / update stack on Azure
aspire do pulumi

# 5. Tear down environment
aspire do pulumi-destroy
```

To deploy to a brand-new environment/resource group (for example `rg-intochat-test2` in `polandcentral`):

```powershell
dotnet run --project src/IntoChat.OS.AppHost/IntoChat.OS.AppHost.csproj -- pulumi --env test2
```
