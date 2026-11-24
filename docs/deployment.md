# Deployment & Pulumi Infrastructure-as-Code

IntoChat OS supports **universal, zero-config-file deployment** to any Azure environment (`--env <name>`, such as `test`, `dev`, or `stage`) using either **Pulumi Infrastructure-as-Code** (`aspire do pulumi`) or direct **Azure Resource Manager (ARM)** provisioning (`install --env <name>`).

No static `deploy/<env>.json` files are required: `InstallConfig.Universal(env, ...)` derives every Azure resource name deterministically from `(prefix, env)` and synthesizes Key Vault secrets and Container App environment variables directly from the composed `OsModule` manifests and `IntegrationSchema` definitions.

---

## 1. Universal Resource Naming (`--env <name>`)

For any environment `<env>` (default `test`) and prefix `<prefix>` (default `intochat`), the stack provisions:

| Resource | Derived Name (`--env test`) | Purpose |
| :--- | :--- | :--- |
| **Resource Group** | `intochat-test` | Holds all foundation and container resources for the environment. |
| **Managed Identity** | `intochat-test-id` | User-assigned identity granted Key Vault, Storage Blob/Table, and ACR pull roles. |
| **Storage Account** | `intochattestst` | Orleans clustering tables, reminders, `grainstate-v1` blob container, `brainlease-v1`, and `brainlog` Azure Files share. |
| **Key Vault** | `intochat-test-kv` | Holds `master-key`, `db-connection`, and every module/integration secret (`{module}-{secret}`). |
| **Container Registry** | `acrintochattest` | Holds the `os` image (and `cloud` image when deploying multi-tenant Cloud). |
| **PostgreSQL Flexible Server** | `intochat-test-pg` | Burstable `Standard_B1ms` (32 GB) relational store wired to `db-connection`. |
| **Log Analytics Workspace** | `intochat-test-log` | Container Apps console and OpenTelemetry log sink. |
| **Container Apps Environment** | `intochat-test-env` | Shared network, mounted `brainlog` file share, and Orleans cluster boundary (`Orleans__ClusterId=intochat-test`). |
| **OS Container App** | `intochat-test-os` | Runs `acrintochattest.azurecr.io/os:latest`: Native AOT Kernel (`/app/silo`), Flutter Web UI + 15 ArtCraft studios (`/app/wwwroot`), and DevKit Roslyn Sandbox (`/app/unithost`). |

> **Note on Multi-Tenant Cloud**: When synthesized from the external Cloud host repository, the same foundation additionally provisions a second Container App (`intochat-<env>-cloud`) in the same `intochat-<env>-env`, storage account and Key Vault, but as its own Orleans cluster with its own storage namespace (`InstallPlan`): cluster and service id `intochat-<env>-cloud`, grain-state container `grainstate-cloud-<format>`, lease container `brainlease-cloud-<format>`, and Home on its own `brainlog-cloud` share at `/data/brain-cloud/<format>`. It publishes no Orleans gateway port. The OS app keeps every name it had. The cloud reaches the OS app only over the OS app's HTTP edge, as a trusted issuer (`IntoChatOS:Auth:TrustedIssuers`, with `Services` and `Guests` for device enrollment and guest try).

---

## 2. How Sandboxes Run Locally vs. in Azure Cloud

There are **zero Azure Container Apps Session Pools (`Microsoft.App/sessionPools`)** in the deployment plan (eliminating the dedicated E16 node cost of ~€67/day). Instead, `HttpSandboxRuntimeBridge` supports three execution modes for `IntoChat.OS.Sandbox`:

1. **Local Development with Aspire (`local` topology)**:
   - `SandboxModuleHosting` runs `IntoChat.OS.Sandbox` inside a hardened local Docker container (`--read-only`, `--cap-drop=ALL`, `--security-opt=no-new-privileges:true`, `--memory=512m`, `--cpus=1.0`, `--tmpfs /tmp:rw,noexec,nosuid,size=64m`).
   - `SandboxKernelProjection` sets `IntoChatOS__Modules__sandbox__Options__Url` on the Kernel to point to the `sandbox` container's HTTP endpoint (`/apps/compile`, `/apps/deploy`, `/apps/dispatch`, `/apps/unload`) authenticated with `X-Sandbox-Enroll-Key`.
2. **Cloud & Desktop Default (`--unit-host` Child Process)**:
   - Inside the unified `os` container image (`src/IntoChat.OS.Kernel/Dockerfile`) and the Windows desktop bundle (`os/` + `unithost/`), `IntoChat.OS.Sandbox` is pre-published at `/app/unithost` (`unithost/IntoChat.OS.Sandbox`).
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

To deploy to a brand-new environment/resource group (for example `intochat-test2` in `polandcentral`):

```powershell
dotnet run --project src/IntoChat.OS.AppHost/IntoChat.OS.AppHost.csproj -- pulumi --env test2
```

## Moving the cloud's records out of an existing install

An install that ran the cloud image before the split keeps the cloud's records (auth, organisations, invites, mail rate, lobby, machine credentials) in the OS app's brain store. Move them once into the cloud's Home with the kernel's `move-inventory` and `move-records` verbs (`MoveInventoryCommand`, `RecordMoveCommand`, over `BrainRecordMove`) and the cloud host's `move-plan` verb. Run the OS verbs in a one-off container from the OS image that mounts both shares (`brainlog` and `brainlog-cloud`) and a folder outside both for the lists and the backup, with `IntoChatOS__MasterKey` set from the vault: they open each brain store with the same key custody as the OS app.

1. Stop both container apps (scale to 0) and wait until no replica runs: the verbs claim each brain store as its writer.
2. List what the OS app's store holds. It changes no record:

   ```
   os move-inventory --home /data/brain/<format> --out /data/split
   ```

   It writes `brains.txt` (one brain id per line: `system` and every brain of the identity directory), `principals.txt` (one principal id per line) and `machines.txt` (one `<brain> <machine id>` per line, every enrolled device that is not revoked). With `machines.txt` the cloud keeps each device's machine credential, so no device enrolls again.
3. Make the plan with the cloud image's `move-plan` verb from `brains.txt` and `machines.txt` (the cloud repository's `ops/cluster-split`), then move:

   ```
   os move-records plan.json plan            # lists what the rules select; writes nothing to the target
   os move-records plan.json backup          # byte copy of every listed brain store, verified by SHA-256
   os move-records plan.json copy            # writes and verifies each selected record in the cloud's Home
   ```

   The plan (`BrainMovePlan`) names `sourceHome`, `targetHome`, `backup`, the `brains` and the `rules` (a neuron type or a grain id prefix). Every phase can run again. A record the cloud's Home already holds with other content is reported as a conflict (exit code 2) and never overwritten.
4. Deploy the split build. Start the Cloud app once with the cloud's principal-links export pointed at `principals.txt`: it reads the moved auth records and writes `principals.txt.links.json`, the `{ issuer, email, principal }` entries of the people it signed up before the split. This must come before step 5, because only the cloud can read those records.
5. Start the OS app once with `IntoChatOS:Auth:PrincipalLinks` naming that `.links.json` file (`PrincipalLinks`). Those people then sign in through the cloud's ticket to the accounts and brains they already have. The result is written beside the file. Remove both settings afterwards; both apps keep running.
6. Only after the cloud has run on its copy: stop both apps, `os move-records plan.json remove-source` clears the copied records from the OS app's store, and only those still identical in both stores.

Rollback: until step 6 no record of the OS app's store is changed by the move (opening a store may compact its journal, as any start does), so going back is deploying the previous images into the previous shape. After step 6, restore the listed brain folders from the backup (`<backup>/brains/<folder>`) with both apps stopped. The principal links only add rows; a linked person also keeps the principal they had.
