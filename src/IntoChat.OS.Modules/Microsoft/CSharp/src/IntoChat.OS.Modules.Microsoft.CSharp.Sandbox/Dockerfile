# syntax=docker/dockerfile:1
# The sandbox host: runtime container built from pre-published host artifacts.
# This eliminates remote NuGet authentication inside Docker, ensuring zero token errors
# and fast, offline-capable local development with exact local code parity.
FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.3
WORKDIR /app
COPY src/IntoChat.OS.Modules/Microsoft/CSharp/src/IntoChat.OS.Modules.Microsoft.CSharp.Sandbox/publish/ .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=1
EXPOSE 8080
ENTRYPOINT ["dotnet", "IntoChat.OS.Modules.Microsoft.CSharp.Sandbox.dll"]
