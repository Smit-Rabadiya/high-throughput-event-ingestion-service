# syntax=docker/dockerfile:1
# Build:  docker build --build-arg GIT_SHA=$(git rev-parse --short HEAD) -t event-ingestion:1.0.0 .
# Pin the base images by digest in your registry/CI (docker buildx imagetools inspect) for fully reproducible builds.
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
ARG GIT_SHA=unknown

# Restore first (cached layer) in locked mode: fails if packages.lock.json is out of date.
COPY global.json Directory.Build.props Directory.Packages.props EventIngestion.slnx ./
COPY src/EventIngestion.Core/*.csproj src/EventIngestion.Core/packages.lock.json src/EventIngestion.Core/
COPY src/EventIngestion.Api/*.csproj src/EventIngestion.Api/packages.lock.json src/EventIngestion.Api/
RUN dotnet restore src/EventIngestion.Api/EventIngestion.Api.csproj --locked-mode

COPY src/ src/
RUN dotnet publish src/EventIngestion.Api/EventIngestion.Api.csproj \
      -c Release --no-restore -o /app/publish \
      -p:UseAppHost=false -p:SourceRevisionId=${GIT_SHA}

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

# Journal directory on a volume the non-root user owns.
RUN mkdir -p /var/lib/ingestion/journal && chown -R $APP_UID:$APP_UID /var/lib/ingestion
COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
VOLUME ["/var/lib/ingestion/journal"]

USER $APP_UID
# Probes: GET /health (liveness) and GET /health/ready (readiness) on port 8080; the runtime image has no curl,
# so configure them in Kubernetes / your orchestrator rather than a Docker HEALTHCHECK.
ENTRYPOINT ["dotnet", "EventIngestion.Api.dll"]
