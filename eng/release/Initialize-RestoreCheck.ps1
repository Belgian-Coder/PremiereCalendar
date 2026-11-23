#Requires -Version 7.0
<#
.SYNOPSIS
Step 2a: creates the isolated, empty PostgreSQL that the fresh backup is restored into.

.DESCRIPTION
Runs only in the local WSL Docker engine (never the NAS engine). Creates one uniquely named internal network with an
explicit /24 subnet and one read-only, tmpfs-only PostgreSQL container without published ports or persistent mounts.
It refuses name collisions and never cleans up automatically; Close-Release.ps1 does not depend on it afterwards.
Remove the two resources yourself after the release is closed (docker rm -f, docker network rm).

.PARAMETER Bundle
Full path of the release bundle created by Initialize-Release.ps1.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Initialize-RestoreCheck.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20
#>
[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string] $Bundle)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
$ctx = Get-ReleaseContext -Bundle $Bundle
$cfg = $ctx.Config
$network = "$($ctx.Prefix)_restore_net"
$db = "$($ctx.Prefix)_restore_db"
$label = "premierecalendar.validation=restore-$($ctx.BundleName)"
$subnet = Get-ReleaseSubnet $ctx 2

$plan = @(
    "Refuse if container $db or network $network already exist",
    "docker network create --internal --subnet $subnet $network (local WSL engine only; on an address overlap up to 5 alternative derived subnets are tried)",
    "docker run --read-only, tmpfs-only (4 GiB data, autovacuum off), no published ports, trust auth inside the internal network: $db from $($cfg.postgresImage)",
    "Wait until pg_isready succeeds")
if (Show-ReleasePlan -Title 'Initialize-RestoreCheck' -Step $plan) { return }

Assert-LocalDockerAvailable
if (-not (Test-LocalDocker image inspect $cfg.postgresImage)) { throw "The pinned PostgreSQL image is not present locally; run: wsl.exe -d Ubuntu-24.04 --exec docker pull $($cfg.postgresImage)" }
Assert-DockerNamesFree -Container $db -Network $network
$subnet = New-ReleaseNetwork -Context $ctx -Offset 2 -Label $label -Name $network -Internal
$null = Invoke-LocalDocker run --pull=never -d --name $db --label $label --network $network --network-alias backup-db --read-only `
    --tmpfs '/var/lib/postgresql:rw,size=4096m' --tmpfs '/tmp:rw,size=64m' --tmpfs '/run/postgresql:rw,size=16m' `
    -e PGDATA=/var/lib/postgresql/data/pgdata -e POSTGRES_USER=postgres -e POSTGRES_DB=premierecalendar -e POSTGRES_HOST_AUTH_METHOD=trust $cfg.postgresImage `
    -c autovacuum=off
# The restored production data plus restore WAL outgrew a 1 GiB tmpfs once autovacuum ran on the idle copy (1.2.21-candidate3:
# "No space left on device", server down before the compatibility check). The copy is throwaway and compared by content,
# so autovacuum is off and the tmpfs has headroom.
Wait-LocalCondition -Attempts 45 -Message 'The isolated PostgreSQL did not become ready.' -Condition { Test-LocalDocker exec $db pg_isready -U postgres -d premierecalendar }
Write-Host "Isolated PostgreSQL ready: $db on internal network $network ($subnet)"
