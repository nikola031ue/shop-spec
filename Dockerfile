# syntax=docker/dockerfile:1
# Shop application image (spec 003, contracts/delivery.md "Image"): the .NET API plus the built
# Angular files, on a non-root chiseled runtime. Base images are pinned by digest.

# 1. Angular build -> /web/out
FROM node:24-bookworm-slim@sha256:190bcf5115694c9d3c4d654a1aa88177d77600a06d67972fd7bf4cf224f7692c AS web
WORKDIR /web
COPY src/Shop.Web/package.json src/Shop.Web/package-lock.json src/Shop.Web/.npmrc ./
RUN npm ci --no-audit --no-fund
COPY src/Shop.Web/ ./
RUN npx ng build --output-path=out

# 2. .NET publish -> /app
FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS api
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Shop.Api/Shop.Api.csproj src/Shop.Api/packages.lock.json src/Shop.Api/
RUN dotnet restore src/Shop.Api/Shop.Api.csproj --locked-mode
COPY src/Shop.Api/ src/Shop.Api/
COPY --from=web /web/out/browser/ src/Shop.Api/wwwroot/
RUN dotnet publish src/Shop.Api/Shop.Api.csproj -c Release --no-restore -o /app /p:UseAppHost=false

# 3. Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:7c625e44e5d588b4d7b2a913e94ca4bd5098ce47442da404829e4c7936a764e1
ARG VERSION=0.0.0-dev
LABEL org.opencontainers.image.source="https://github.com/nikola031ue/shop-spec" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.title="shop"
WORKDIR /app
COPY --from=api /app ./
# Port 80 as non-root relies on net.ipv4.ip_unprivileged_port_start=0 (set by shop-operator 0.2.0
# and by Docker's default container sysctls).
ENV ASPNETCORE_HTTP_PORTS=80
USER $APP_UID
# 80: the shop (Ingress). 8081: the operator's internal API, only with INTERNAL_TOKEN (spec 005).
EXPOSE 80 8081
ENTRYPOINT ["dotnet", "Shop.Api.dll"]
