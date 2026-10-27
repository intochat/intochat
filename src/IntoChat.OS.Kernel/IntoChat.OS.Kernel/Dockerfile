# syntax=docker/dockerfile:1
#
# The operating system image.
# Production pushes use SDK PublishContainer from .github/workflows/release.yml (single
# ghcr.io/intochat/os image). This Dockerfile is the local/smoke equivalent and must stay in
# step with the product AppHost profile.
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
#   ConnectionStrings__qdrant  Endpoint=http://qdrant:6334;Key=...
#
# Ports: 8080 silo HTTP (broker + health).

FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.3 AS build
WORKDIR /src

COPY nuget.config Directory.Build.props Directory.Build.targets Directory.Packages.props Intochat.OS.slnx global.json VERSION .editorconfig ./
COPY framework/ ./framework/
COPY src/ ./src/

RUN dotnet publish src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj \
      -c Release \
      -r linux-x64 \
      --self-contained false \
      -p:CodeGraphRefresh=false \
      -o /out/silo \
      --nologo

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.3 AS final
WORKDIR /app

COPY --from=build /out/silo /app/silo
COPY features /app/features

# The image enables the OS catalog; configuration may disable modules
# (IntoChatOS__Modules__{id}__Enabled=false).
# A production own machine (IntoChatOS__Platform__Parts__Role=Own) must set IntoChatOS__Platform__Parts__Custody=Tpm
# (Windows + TPM) or IntoChatOS__Platform__Parts__ConfiguredCustodyAcknowledged=true, or it refuses to start (R81).
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    IntoChatOS__Topology=cloud \
    DOTNET_EnableDiagnostics=0 \
    IntoChatOS__Modules__apps__Options__Features=/app/features

EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "/app/silo/IntoChat.OS.Kernel.dll"]
