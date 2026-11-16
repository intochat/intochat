# syntax=docker/dockerfile:1
#
# Isolated IntoChat OS Sandbox container worker.
#
# Build (repo root):
#   docker build -f src/IntoChat.OS.Sandbox/Dockerfile -t intochat-os-sandbox:local .

FROM mcr.microsoft.com/dotnet/sdk:11.0.100 AS build
WORKDIR /src

COPY nuget.config Directory.Build.props Directory.Build.targets Directory.Packages.props Intochat.OS.slnx global.json VERSION .editorconfig ./
COPY src/IntoChat.OS/ ./src/IntoChat.OS/
COPY src/IntoChat.OS.UI/ ./src/IntoChat.OS.UI/
COPY src/IntoChat.OS.Sandbox/ ./src/IntoChat.OS.Sandbox/

ARG NUGET_TOKEN=""
ARG LOCAL_FEED_SUBDIR="src/IntoChat.OS.Sandbox"
COPY ${LOCAL_FEED_SUBDIR}/ ./local-feed/

RUN --mount=type=cache,target=/root/.nuget/packages \
    --mount=type=secret,id=nuget_token,required=false \
    TOKEN="${NUGET_TOKEN}"; \
    if [ -z "${TOKEN}" ] && [ -f /run/secrets/nuget_token ] && [ -s /run/secrets/nuget_token ]; then \
        TOKEN="$(cat /run/secrets/nuget_token)"; \
    fi; \
    if ls /src/local-feed/*.nupkg >/dev/null 2>&1; then \
        dotnet nuget update source local-feed --source /src/local-feed --configfile nuget.config >/dev/null 2>&1 || true; \
    else \
        dotnet nuget remove source local --configfile nuget.config >/dev/null 2>&1 || true; \
        dotnet nuget remove source local-feed --configfile nuget.config >/dev/null 2>&1 || true; \
    fi; \
    if [ -n "${TOKEN}" ]; then \
        dotnet nuget update source github \
            --username "intochat" \
            --password "${TOKEN}" \
            --store-password-in-clear-text \
            --configfile nuget.config >/dev/null 2>&1 || true; \
    fi \
    && dotnet publish src/IntoChat.OS.Sandbox/IntoChat.OS.Sandbox.csproj \
      -c Release \
      --no-self-contained \
      -p:CodeGraphRefresh=false \
      -p:DebugType=none \
      -o /out/sandbox \
      --nologo \
    && rm -rf /src/artifacts /src/nuget.config /src/local-feed

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0 AS final
WORKDIR /app

COPY --from=build /out/sandbox /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

USER app
ENTRYPOINT ["dotnet", "IntoChat.OS.Sandbox.dll"]
