# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-preview.7-alpine3.24@sha256:186cbf87f5b66f2e4ff937b6a3cd420b005356e940eacd5c420fc30308c49c46 AS build
WORKDIR /src
COPY global.json Directory.Build.props PremiereCalendar.slnx ./
COPY PremiereCalendar/PremiereCalendar.csproj PremiereCalendar/
COPY tests/PremiereCalendar.UnitTests/PremiereCalendar.UnitTests.csproj tests/PremiereCalendar.UnitTests/
COPY tests/PremiereCalendar.ComponentTests/PremiereCalendar.ComponentTests.csproj tests/PremiereCalendar.ComponentTests/
COPY tests/PremiereCalendar.IntegrationTests/PremiereCalendar.IntegrationTests.csproj tests/PremiereCalendar.IntegrationTests/
COPY tests/PremiereCalendar.BrowserTests/PremiereCalendar.BrowserTests.csproj tests/PremiereCalendar.BrowserTests/
RUN dotnet restore PremiereCalendar/PremiereCalendar.csproj --nologo
COPY PremiereCalendar/ PremiereCalendar/
COPY deploy/Updates/ deploy/Updates/
ARG VERSION=0.0.0-container
ARG SOURCE_REVISION=unknown
ARG BUILD_ID=container
ARG BUILD_TIME_UTC=unknown
RUN dotnet publish PremiereCalendar/PremiereCalendar.csproj -c Release --no-restore -o /out \
    /p:Version="$VERSION" /p:InformationalVersion="$VERSION+$BUILD_ID" \
    /p:SourceRevisionId="$SOURCE_REVISION" /p:BuildId="$BUILD_ID" /p:BuildTimeUtc="$BUILD_TIME_UTC" \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/sdk:11.0.100-preview.7-alpine3.24@sha256:186cbf87f5b66f2e4ff937b6a3cd420b005356e940eacd5c420fc30308c49c46 AS development
WORKDIR /src
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 DOTNET_USE_POLLING_FILE_WATCHER=1
EXPOSE 8080
CMD ["dotnet", "watch", "--project", "PremiereCalendar/PremiereCalendar.csproj", "run", "--no-launch-profile"]

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-preview.7-alpine3.24@sha256:994a3d79e5e49277adb008b6272a3b90dd2b7c2e9709e62c9d70738df3bbcc06 AS runtime
ARG VERSION=0.0.0-container
ARG SOURCE_REVISION=unknown
ARG BUILD_TIME_UTC=unknown
LABEL org.opencontainers.image.title="PremiereCalendar" \
      org.opencontainers.image.source="https://github.com/Belgian-Coder/PremiereCalendar" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$SOURCE_REVISION" \
      org.opencontainers.image.created="$BUILD_TIME_UTC"
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0 \
    COMPlus_EnableDiagnostics=0
COPY --from=build --chown=$APP_UID:$APP_UID /out/ ./
RUN mkdir -p /app/App_Data && chown -R $APP_UID:$APP_UID /app/App_Data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "PremiereCalendar.dll"]
