# syntax=docker/dockerfile:1
#
# The operating system image.
# Production pushes use SDK PublishContainer from .github/workflows/deploy.yml (single
# digitalbrain-kernel image). This Dockerfile is the local/smoke equivalent and must stay in
# step with the product AppHost profile.
#
# Build (repo root):
#   docker build -f src/DigitalBrain/OS/DigitalBrain.OS/Dockerfile -t vhorbachov/digitalbrain-kernel:local .
#
# No secrets are baked into this image. Managed identity + Key Vault are injected at run/deploy:
#
#   AZURE_CLIENT_ID                 user-assigned managed identity for the container app
#   DigitalBrain__KeyVault__Uri     Key Vault URI resolved by the managed identity (config only)
#   ConnectionStrings__clustering   Orleans clustering (Azure Table/Blob)
#   ConnectionStrings__reminders    Orleans reminders
#   ConnectionStrings__grainstate   Orleans grain state (digitalbrain-v2-state)
#   ConnectionStrings__qdrant  Endpoint=http://qdrant:6334;Key=...
#
# Ports: 8080 silo HTTP (broker + health).

FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.1 AS build
WORKDIR /src

COPY nuget.config Directory.Build.props Directory.Packages.props DigitalBrain.slnx global.json ./
COPY src/ ./src/

RUN dotnet publish src/DigitalBrain/OS/DigitalBrain.OS/DigitalBrain.OS.csproj \
      -c Release \
      -r linux-x64 \
      --self-contained false \
      -o /out/silo \
      --nologo

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.1 AS final
WORKDIR /app

COPY --from=build /out/silo /app/silo
COPY features /app/features

# The image enables the OS catalog; configuration may disable modules
# (DigitalBrain__Modules__{id}__Enabled=false).
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0 \
    DigitalBrain__Modules__apps__Options__Features=/app/features

EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "/app/silo/DigitalBrain.OS.dll"]
