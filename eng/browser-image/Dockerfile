# Prepared Linux/amd64 browser image for the Browser test suite (glibc profile).
#
# The official Playwright image supplies Chromium, Firefox, WebKit and their system libraries for the Playwright
# version this project pins (Microsoft.Playwright.Xunit.v3 in tests/PremiereCalendar.BrowserTests); the .NET SDK
# that global.json pins is copied in from the official SDK image of the same Ubuntu release. Keep the Playwright tag
# equal to the pinned package version: a package bump needs a new image with matching browser builds.
# Build and run commands: docs/Testing.md ("Prepared browser image").
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-resolute@sha256:2794448a07118a6eaf00f29ab5d2bf44928517ce5ff86505a63a69230374f599 AS sdk

FROM mcr.microsoft.com/playwright/dotnet:v1.64.1-resolute@sha256:56604962ec1853702347ba35d63edbfb598714f1728ef1e71b399c5f12bc2866
COPY --from=sdk /usr/share/dotnet/ /usr/share/dotnet/
ENV DOTNET_ROOT=/usr/share/dotnet \
    PATH=/usr/share/dotnet:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    DOTNET_GENERATE_ASPNET_CERTIFICATE=false \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
