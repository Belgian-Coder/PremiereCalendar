# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-alpine3.24@sha256:52556c0fb9d0f609ae9aeab9efeeba96f4acc2f7038f7696f2235ccbdbb8e290 AS build
WORKDIR /src
COPY global.json Directory.Build.props PremiereCalendar.slnx ./
COPY PremiereCalendar/PremiereCalendar.csproj PremiereCalendar/
# The package cache lives outside the image (BuildKit cache mount), so a changed project file re-downloads only new
# packages. It never enters an image layer; publish mounts it again because --no-restore still reads the packages.
ENV NUGET_PACKAGES=/nuget-packages
RUN --mount=type=cache,id=premierecalendar-nuget,target=/nuget-packages \
    dotnet restore PremiereCalendar/PremiereCalendar.csproj --nologo
COPY PremiereCalendar/ PremiereCalendar/
ARG VERSION=0.0.0-container
ARG SOURCE_REVISION=unknown
ARG BUILD_ID=container
ARG BUILD_TIME_UTC=unknown
RUN --mount=type=cache,id=premierecalendar-nuget,target=/nuget-packages \
    dotnet publish PremiereCalendar/PremiereCalendar.csproj -c Release --no-restore -o /out \
    /p:Version="$VERSION" /p:InformationalVersion="$VERSION+$BUILD_ID" \
    /p:SourceRevisionId="$SOURCE_REVISION" /p:BuildId="$BUILD_ID" /p:BuildTimeUtc="$BUILD_TIME_UTC" \
    /p:UseAppHost=false \
    && test -s /out/wwwroot/_framework/blazor.web.js

FROM mcr.microsoft.com/dotnet/sdk:11.0.100-alpine3.24@sha256:52556c0fb9d0f609ae9aeab9efeeba96f4acc2f7038f7696f2235ccbdbb8e290 AS development
WORKDIR /src
ENV Urls=http://0.0.0.0:8080 DOTNET_USE_POLLING_FILE_WATCHER=1
EXPOSE 8080
CMD ["dotnet", "watch", "--project", "PremiereCalendar/PremiereCalendar.csproj", "run", "--no-launch-profile"]

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-alpine3.24@sha256:d39b72ad1534fa0faf52b488bffb0762df7ba269d120f0338713d4dda2a41bf5 AS runtime
ARG VERSION=0.0.0-container
ARG SOURCE_REVISION=unknown
ARG BUILD_TIME_UTC=unknown
LABEL org.opencontainers.image.title="PremiereCalendar" \
      org.opencontainers.image.source="https://github.com/Belgian-Coder/PremiereCalendar" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$SOURCE_REVISION" \
      org.opencontainers.image.created="$BUILD_TIME_UTC"
WORKDIR /app
ENV Urls=http://0.0.0.0:8080 \
    HOME=/tmp \
    DOTNET_EnableDiagnostics=0 \
    COMPlus_EnableDiagnostics=0
COPY --from=build --chown=$APP_UID:$APP_UID /out/ ./
# Nightly schedules use IANA zones. Reuse timezone data from the pinned SDK
# image so the runtime supports Brussels/DST and configurable zone overrides.
COPY --from=build /usr/share/zoneinfo/ /usr/share/zoneinfo/
# The pinned base still ships OpenSSL 3.5.9-r0; take the fixed Alpine packages explicitly. The version floor makes the
# build fail rather than silently ship the vulnerable libraries if the repository cannot supply them. Drop this line once
# the pinned base image contains 3.5.9-r1 or later.
RUN apk add --no-cache --upgrade 'libcrypto3>=3.5.9-r1' 'libssl3>=3.5.9-r1' \
    && mkdir -p /app/App_Data && chown -R $APP_UID:$APP_UID /app/App_Data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "PremiereCalendar.dll"]
