# syntax=docker/dockerfile:1

# Reignbird as a self-contained Linux image, for linux/amd64 and linux/arm64.
#
# Two decisions worth explaining, because both are easy to get wrong:
#
# 1. Every build stage that actually runs commands is pinned to $BUILDPLATFORM, and
#    only the final stage takes the target platform. Nothing is ever executed under
#    emulation, so `--platform linux/arm64` on an x64 machine costs no more than a
#    native build. The .NET SDK cross-publishes with -a, and the final stage only
#    copies files, so QEMU is never needed. Build both arches in about the time one
#    would otherwise take.
#
# 2. The base is `-extra`, not the smaller plain chiseled image. The extra variant
#    carries ICU and tzdata, and this app cannot work without tzdata: it schedules
#    watering in local time via TimeZoneInfo.FindSystemTimeZoneById, and offers a
#    time zone picker built from TimeZoneInfo.GetSystemTimeZones(). Without the tz
#    database that picker is empty and every lookup silently falls back to UTC —
#    which does not fail, it just waters the lawn at the wrong time of day.

ARG DOTNET_VERSION=10.0
# Node 24 because that is what generated package-lock.json. An older image ships an
# older npm, and the two resolve a couple of pre-release transitive dependencies
# differently — enough for `npm ci` to declare the lockfile out of sync and stop.
ARG NODE_VERSION=24


# ---------------------------------------------------------------- SPA -------
# Runs on the builder's own architecture: the output is JavaScript, which does not
# care what CPU the image will eventually run on.

FROM --platform=$BUILDPLATFORM node:${NODE_VERSION}-alpine AS spa
WORKDIR /src/web

# Lockfile first, so `npm ci` is only re-run when dependencies actually change.
COPY web/package.json web/package-lock.json ./
RUN npm ci

COPY web/ ./
# vite.config.ts writes to ../src/RainBird.Server/wwwroot, i.e. /src/src/... here.
RUN npm run build


# ------------------------------------------------------------- publish ------

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
WORKDIR /src

COPY src/ ./src/
COPY --from=spa /src/src/RainBird.Server/wwwroot ./src/RainBird.Server/wwwroot

# Docker says "amd64"; .NET says "x64". Everything else the two agree on.
RUN DOTNET_ARCH="$(case "$TARGETARCH" in amd64) echo x64 ;; *) echo "$TARGETARCH" ;; esac)" \
 && dotnet publish src/RainBird.Server/RainBird.Server.csproj \
      --configuration Release \
      --arch "$DOTNET_ARCH" \
      --self-contained true \
      -p:SkipSpaBuild=true \
      -o /app/publish

# Created here so they arrive in the final image already owned by the app user.
# The final image has no shell, so there is no mkdir available there.
RUN mkdir -p /state/store /state/media


# --------------------------------------------------------------- final ------
# No --platform: this one resolves to the architecture being built for.
#
# runtime-deps rather than aspnet, because a self-contained publish brings its own
# copy of .NET and would only be shadowed by a runtime in the base image.

FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION}-noble-chiseled-extra AS final
WORKDIR /app

COPY --from=build --chown=$APP_UID:$APP_UID /app/publish ./
COPY --from=build --chown=$APP_UID:$APP_UID /state/store ./store
COPY --from=build --chown=$APP_UID:$APP_UID /state/media ./media

# The base image presets 8080; appsettings.json asks for 5056. Pinning both to the
# same number means the answer does not depend on which one wins.
ENV ASPNETCORE_HTTP_PORTS=5056

# Workstation GC. Server GC assumes it should use the whole machine, which for an
# app that talks to one sprinkler controller is a lot of memory to hold for nothing.
ENV DOTNET_gcServer=0

# Set this to your own zone, or the app defaults new controllers to UTC.
ENV TZ=Etc/UTC

EXPOSE 5056

# store/ holds the SQLite database and the Data Protection keys that encrypt
# controller passwords; media/ holds zone photos. Lose either and controllers have
# to be added again.
VOLUME ["/app/store", "/app/media"]

ENTRYPOINT ["/app/RainBird.Server"]
