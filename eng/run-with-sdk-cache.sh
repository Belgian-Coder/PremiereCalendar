#!/bin/sh
# Only SDK CLI/plugin state belongs here; package scratch/HTTP remain with packages.
set -eu
cache=/build/.sdk-cache
build=${cache%/*}
owner=${PC_SDK_CACHE_OWNER-}
case "$owner" in ''|*[!A-Za-z0-9_.-]*) echo 'PC_SDK_CACHE state=invalid-owner' >&2; exit 29 ;; esac
[ "$#" -gt 0 ] || { echo 'PC_SDK_CACHE state=missing-command' >&2; exit 29; }
expected="PremiereCalendarSdkCache:v1:$owner"
unknown() { echo "PC_SDK_CACHE state=unknown-preserved path=$cache" >&2; exit 29; }
[ ! -L "$build" ] && [ ! -L "$cache" ] || unknown
if [ -e "$build" ]; then
    [ -d "$build" ] || unknown
else
    mkdir "$build" || unknown
fi
if [ -e "$cache" ]; then
    [ -d "$cache" ] && [ -f "$cache/.owner" ] && [ ! -L "$cache/.owner" ] || unknown
    printf '%s' "$expected" | cmp -s "$cache/.owner" - || unknown
else
    # A concurrent/partial initialization is preserved and refused, never adopted.
    (umask 077; mkdir "$cache" && printf '%s' "$expected" > "$cache/.owner") || unknown
fi
for directory in "$cache/dotnet" "$cache/nuget-plugins"; do
    [ ! -L "$directory" ] || unknown
    [ ! -e "$directory" ] || [ -d "$directory" ] || unknown
done
mkdir -p "$cache/dotnet" "$cache/nuget-plugins"
export DOTNET_CLI_HOME="$cache/dotnet" NUGET_PLUGINS_CACHE_PATH="$cache/nuget-plugins"
# SDK first-use state is local to this build lane. Never prompt for credentials
# or install an HTTPS development certificate when entering a fresh CLI home.
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false NUGET_EXE_NO_PROMPT=true NuGetInteractive=false
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
exec "$@"
