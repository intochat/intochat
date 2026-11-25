# Self-Hosting IntoChat OS & .NET Aspire Integration

Run IntoChat OS on your own server in a container or compose it inside your own .NET Aspire AppHost. For a product overview, desktop/mobile downloads, and local contributor setup, return to [`README.md`](../README.md). For Azure cloud provisioning and Pulumi Infrastructure-as-Code, see [Deployment & Pulumi IaC](deployment.md).

---

## Container Hosting Overview

The unified IntoChat OS container image (`ghcr.io/intochat/os:latest`) bundles the Native AOT kernel, the embedded Flutter web shell, the Roslyn sandbox unit host, and ArtCraft web bundles in a single image.

- **Image:** `ghcr.io/intochat/os:latest`
- **Container HTTP port:** `8080`
- **Health check endpoint:** `GET /health`

### Quick Run with Docker

```bash
docker run -d --name intochat-os \
  -p 5080:8080 \
  -v intochat-home:/app/home \
  -e IntoChatOS__Home=/app/home \
  -e IntoChatOS__Topology=local \
  -e IntoChatOS__Auth__Username=operator \
  -e IntoChatOS__Auth__Password=change-me-strong-secret \
  ghcr.io/intochat/os:latest
```

Once healthy (`http://localhost:5080/health`), open `http://localhost:5080` in your browser or point any IntoChat desktop/mobile shell at your server URL.

### Core Environment Variables

| Environment Variable | Default | Description |
| :--- | :--- | :--- |
| `IntoChatOS__Home` | `~/.intochat` | Root directory for brain stores, workspaces, and sealed state. Mount a persistent volume (for example `/app/home`) when running in containers. |
| `IntoChatOS__Topology` | `local` | Kernel runtime topology (`local`, `cloud`, etc.) governing which topology-scoped modules load. |
| `IntoChatOS__Auth__Username` | *(unset)* | Bootstrap owner username. Required when listening beyond loopback without a sign-in module (unless `IntoChatOS__Auth__AllowOpenOnNetwork=true`). |
| `IntoChatOS__Auth__Password` | *(unset)* | Bootstrap owner password paired with `IntoChatOS__Auth__Username`. |
| `IntoChatOS__Auth__AllowOpenOnNetwork` | `false` | Explicitly permits starting without bootstrap credentials or a sign-in module when listening beyond loopback. Keep `false` on untrusted networks. |

> **Network security guard:** A kernel that binds beyond loopback (as the container image does) and has no sign-in module refuses to start unless both `IntoChatOS__Auth__Username` and `IntoChatOS__Auth__Password` are configured, or `IntoChatOS__Auth__AllowOpenOnNetwork=true` is explicitly set.

---

## .NET Aspire AppHost Integration

An Aspire AppHost can orchestrate the IntoChat OS container via `IntoChatOSHostingExtensions` (`IntoChat.OS.Aspire.Hosting`, defined in [`src/IntoChat.OS.AppHost/Hosting/IntoChatOSHostingExtensions.cs`](../src/IntoChat.OS.AppHost/Hosting/IntoChatOSHostingExtensions.cs)):

```csharp
using IntoChat.OS.Aspire.Hosting;

var os = builder.AddIntoChatOSContainer("os", httpPort: 5080)
    .WithImageTag("latest")
    .WithTopology("local")
    .WithBootstrapOwner("operator", "bootstrap-secret")
    .WithEnvironment("IntoChatOS__Home", "/app/home");
```

### Extension Methods

- **`AddIntoChatOSContainer(name, image, tag, httpPort)`** — defaults to `image: "ghcr.io/intochat/os"` and `tag: "latest"`, exposes the `"http"` endpoint targeting container port `8080` (bound to `httpPort` when supplied), and registers an HTTP health check on `/health`.
- **`WithBootstrapOwner(username, password)`** — sets `IntoChatOS__Auth__Username` and `IntoChatOS__Auth__Password` on the container resource.
- **`WithTopology(topology)`** — sets `IntoChatOS__Topology`.

[`IntoChat.OS.AppHost`](../src/IntoChat.OS.AppHost/IntoChat.OS.AppHost.csproj) is marked `IsPackable=true` and is distributed as a NuGet package for external AppHosts (such as IntoChat Cloud).

### Building Your Own Custom AppHost

To compose a source-built kernel with custom module defaults and model routing directly in an Aspire AppHost:

```csharp
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Modules.AI.Models;

builder.AddIntoChatOS("Modules")
    .WithAI(ai => ai.Cloud(c => c.Roles(r => r.Chat(KnownModels.DeepSeekV41Flash))))
    .AddKernel("../IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj");
```

Values configured in the AppHost chain act as defaults that can be overridden at runtime via `IntoChatOS:Modules:<id>:Options` (see [Modules](modules.md)).

---

## Packages & Distribution Matrix

The [release workflow](../.github/workflows/release.yml) builds and publishes packages, images, and binaries whenever a release tag `v<VERSION>` (matching [`VERSION`](../VERSION), applied solution-wide via [`Directory.Build.props`](../Directory.Build.props)) is published. See [`CHANGELOG.md`](../CHANGELOG.md) for release notes.

| Artifact | Source | Distribution Target(s) |
| :--- | :--- | :--- |
| **`DigitalBrain.*` & `DigitalBrain.Templates` NuGet packages** | [`framework/DigitalBrain.slnx`](../framework/DigitalBrain.slnx), [`framework/templates/DigitalBrain.Templates/DigitalBrain.Templates.csproj`](../framework/templates/DigitalBrain.Templates/DigitalBrain.Templates.csproj) | GitHub Packages (`https://nuget.pkg.github.com/intochat/index.json`), `nuget.org` (`https://api.nuget.org/v3/index.json`, when configured), and GitHub Release assets. |
| **`IntoChat.OS.AppHost` NuGet package** | [`src/IntoChat.OS.AppHost/IntoChat.OS.AppHost.csproj`](../src/IntoChat.OS.AppHost/IntoChat.OS.AppHost.csproj) (`IsPackable=true`) | GitHub Packages (`https://nuget.pkg.github.com/intochat/index.json`), `nuget.org` (when configured), and GitHub Release assets. |
| **OS Container Image (`ghcr.io/intochat/os`)** | [`src/IntoChat.OS.Kernel/Dockerfile`](../src/IntoChat.OS.Kernel/Dockerfile) & [`Container.pubxml`](../src/IntoChat.OS.Kernel/Properties/PublishProfiles/Container.pubxml) | GitHub Packages (`ghcr.io/intochat/os`, default in `Container.pubxml` and `AddIntoChatOSContainer`); release pipeline also pushes `acrintochattest.azurecr.io/os:<VERSION>` & `:latest` plus `release-manifest.json`. |
| **Sandbox & ArtCraft Container Images** | [`src/IntoChat.OS.Sandbox/Dockerfile`](../src/IntoChat.OS.Sandbox/Dockerfile) & [`.github/workflows/artcraft-images.yml`](../.github/workflows/artcraft-images.yml) | Prebuilt `ghcr.io/intochat/os-artcraft-<app>-<mcp\|web>` images on GitHub Packages; sandbox image `acrintochattest.azurecr.io/sandbox:<VERSION>` & `:latest`. |
| **Self-Contained Kernel Binary** | [`src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj`](../src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj) (`linux-x64`) | Published as `kernel-bin.tar.gz` release artifact. |
| **Desktop & Mobile Client Bundles** | [`eng/package.cs`](../eng/package.cs) (`dotnet run eng/package.cs --platform <platform> --version <ver>`) | GitHub Releases (versioned & stable versionless Windows `.exe`/`.zip`, macOS `.dmg`/`.tar.gz`, Linux `.deb`/`.tar.gz`, Android `.apk`, unsigned iOS `.ipa`, `SHA256SUMS`, `latest.json`, `winget/IntoChat.OS.yaml`, `homebrew/intochat.rb`, `install.ps1`, `install.sh`). |

---

## See Also

- [`README.md`](../README.md) — Product overview, instant browser try, direct downloads, and contributor quick start.
- [Deployment & Pulumi IaC](deployment.md) — Universal Azure environment provisioning, sandbox execution modes, and `aspire do pulumi` workflows.
- [Modules](modules.md) — Module options, secrets, and the `1 Folder = 1 Module` layout.
- [Documentation Index](index.md) — Complete product documentation map.
