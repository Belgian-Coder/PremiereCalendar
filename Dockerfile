# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.3-alpine3.24@sha256:f2542b156e7c9eda7783e4a5745efa6c133a162a1940fd4175849aa80fccfd23 AS build
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
    /p:UseAppHost=false \
    && test -s /out/wwwroot/_framework/blazor.web.js

FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.3-alpine3.24@sha256:f2542b156e7c9eda7783e4a5745efa6c133a162a1940fd4175849aa80fccfd23 AS development
WORKDIR /src
ENV Urls=http://0.0.0.0:8080 DOTNET_USE_POLLING_FILE_WATCHER=1
EXPOSE 8080
CMD ["dotnet", "watch", "--project", "PremiereCalendar/PremiereCalendar.csproj", "run", "--no-launch-profile"]

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.3-alpine3.24@sha256:77256017a4f675e1cf66a4e910e115608a73a044b50ee07c3d042c8a71132da5 AS runtime
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
    DOTNET_EnableDiagnostics=0 \
    COMPlus_EnableDiagnostics=0
COPY --from=build --chown=$APP_UID:$APP_UID /out/ ./
RUN mkdir -p /app/App_Data && chown -R $APP_UID:$APP_UID /app/App_Data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "PremiereCalendar.dll"]
