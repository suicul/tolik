# TOLIK Core — server image for VPS deployment (AGENTS.md §55).
# Build context = repository root:
#   docker build -f src/TOLIK.Core.Host/Dockerfile -t tolik-core .

# -- Build stage -------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy only project files first for layer-cached restore.
COPY TOLIK.sln ./
COPY Directory.Build.props ./
COPY src/TOLIK.Contracts/TOLIK.Contracts.csproj src/TOLIK.Contracts/
COPY src/TOLIK.Core/TOLIK.Core.csproj src/TOLIK.Core/
COPY src/TOLIK.Infrastructure/TOLIK.Infrastructure.csproj src/TOLIK.Infrastructure/
COPY src/TOLIK.Core.Host/TOLIK.Core.Host.csproj src/TOLIK.Core.Host/
RUN dotnet restore src/TOLIK.Core.Host/TOLIK.Core.Host.csproj

# Full sources and publish.
COPY src/ src/
RUN dotnet publish src/TOLIK.Core.Host/TOLIK.Core.Host.csproj \
    -c Release \
    -o /app/publish \
    -p:UseAppHost=false \
    --no-restore

# -- Runtime stage ------------------------------------------------------------
# NOTE: keep this stage free of apt-get — the sandbox network filters Ubuntu
# archives. The base image already provides bash (used by the compose
# healthcheck); no wget/curl is available or required.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
USER $APP_UID

# wwwroot must exist in publish output; create as fallback for bare images.
RUN mkdir -p /app/wwwroot

ENV ASPNETCORE_URLS=http://+:5080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 5080

# Core applies EF migrations on startup (AddTolikPostgres).
# Non-root per container hardening.
USER $APP_UID
ENTRYPOINT ["dotnet", "TOLIK.Core.Host.dll"]
