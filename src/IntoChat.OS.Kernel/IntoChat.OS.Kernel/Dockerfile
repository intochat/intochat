# syntax=docker/dockerfile:1
#
# The unified IntoChat OS image (Kernel + embedded Flutter web shell + ArtCraft bundles).
#
# Build (repo root):
#   docker build -f src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/Dockerfile -t os:local .
#
# No secrets are baked into this image. Managed identity + Key Vault are injected at run/deploy:
#
#   AZURE_CLIENT_ID                 user-assigned managed identity for the container app
#   IntoChatOS__Topology            cloud
#   IntoChatOS__Cloud__Storage      the storage account (blob URI: clustering, reminders, grain state, signal queues)
#   IntoChatOS__Cloud__KeyVault     Key Vault URI resolved by the managed identity (secrets as configuration)
#   ConnectionStrings__qdrant       Endpoint=http://qdrant:6334;Key=...
#
# Ports: 8080 silo HTTP (static web shell + broker + health).

FROM debian:bookworm-slim AS ui-build
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl git unzip xz-utils \
    && rm -rf /var/lib/apt/lists/*
ARG FLUTTER_VERSION=3.47.2
RUN curl -fsSL "https://storage.googleapis.com/flutter_infra_release/releases/stable/linux/flutter_linux_${FLUTTER_VERSION}-stable.tar.xz" \
        | tar -xJ -C /opt \
    && git config --global --add safe.directory /opt/flutter
ENV PATH="/opt/flutter/bin:${PATH}"
RUN flutter --version && flutter precache --web
WORKDIR /workspace
COPY ui/pubspec.yaml ui/pubspec.lock ./
RUN flutter pub get --enforce-lockfile
COPY ui/ ./
RUN flutter build web --release --base-href "/" --no-tree-shake-icons --no-pub \
    && rm -rf /workspace/.dart_tool \
    && for spec in \
        "photocraft:0.21.0" \
        "filmcraft:0.18.0" \
        "vectorcraft:0.22.0" \
        "lightcraft:0.19.0" \
        "effectcraft:0.22.0" \
        "designcraft:0.18.0" \
        "pdfcraft:0.19.0" \
        "wordcraft:0.16.0" \
        "soundcraft:0.18.0" \
        "deckcraft:0.16.0" \
        "cadcraft:0.18.0" \
        "gridcraft:0.17.0" \
        "figcraft:0.16.0" \
        "texturecraft:0.4.0" \
        "materialcraft:0.2.0"; do \
      app="${spec%%:*}"; ver="${spec##*:}"; \
      mkdir -p "/workspace/build/web/artcraft/${app}" "/tmp/artcraft-${app}"; \
      curl -fsSL "https://github.com/storytold/${app}/releases/download/v${ver}/${app}-web-${ver}.zip" -o "/tmp/artcraft-${app}.zip" \
      && unzip -q "/tmp/artcraft-${app}.zip" -d "/tmp/artcraft-${app}" \
      && cp -r "/tmp/artcraft-${app}/${app}-web-${ver}/." "/workspace/build/web/artcraft/${app}/" \
      && sed -i "s|<head>|<head><base href=\"/artcraft/${app}/\">|" "/workspace/build/web/artcraft/${app}/index.html" \
      && rm -rf "/tmp/artcraft-${app}" "/tmp/artcraft-${app}.zip"; \
    done

FROM mcr.microsoft.com/dotnet/sdk:11.0.100 AS build
WORKDIR /src
ARG GITHUB_ACTOR=intochat

COPY nuget.config Directory.Build.props Directory.Build.targets Directory.Packages.props Intochat.OS.slnx global.json VERSION .editorconfig ./
COPY src/ ./src/
COPY framework/ ./framework/

RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj \
      --configfile nuget.config \
      -c Release \
      -r linux-x64 \
      --self-contained false \
      -p:CodeGraphRefresh=false \
      -p:DebugType=none \
      -o /out/silo \
      --nologo \
    && rm -rf /src/artifacts

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0 AS final
WORKDIR /app

COPY --from=build /out/silo /app/silo
COPY --from=ui-build /workspace/build/web /app/wwwroot

# The image enables the OS catalog; configuration may disable modules
# (IntoChatOS__Modules__{id}__Enabled=false).
# A production own machine (IntoChatOS__Platform__Parts__Role=Own) must set IntoChatOS__Platform__Parts__Custody=Tpm
# (Windows + TPM) or IntoChatOS__Platform__Parts__ConfiguredCustodyAcknowledged=true, or it refuses to start (R81).
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    IntoChatOS__Topology=cloud \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "/app/silo/IntoChat.OS.Kernel.dll"]
