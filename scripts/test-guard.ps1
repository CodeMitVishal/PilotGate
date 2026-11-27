<#
.SYNOPSIS
  Runs DIAL Guard's offline tests (unit, property, app-level through the real SDK) inside the pinned Linux image the
  guard ships in, with hash-locked dependencies. The same command runs locally and on the CI agent.
#>
param(
    [string]$Image = "python:3.12-slim@sha256:191083cdf6c7bda8c76158983163ead5d5cf7b77cf3033e805bb0f5296f51b4b",
    [int]$FuzzScale = 1
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# Windows PowerShell turns redirected native stderr into a terminating error under Stop; check with Continue.
$ErrorActionPreference = "Continue"
docker info --format "{{.ServerVersion}}" *> $null
$dockerUp = $LASTEXITCODE -eq 0
$ErrorActionPreference = "Stop"
if (-not $dockerUp) {
    Write-Host "Docker is not running; the guard's tests run in a Linux container." -ForegroundColor Red
    exit 2
}

$install = "pip install -q --root-user-action=ignore --disable-pip-version-check --require-hashes -r requirements.lock -r requirements-dev.lock"
docker run --rm -v "${repo}:/w" -w /w/dial/guard -e PYTHONPATH=/w/dial/guard/src -e PYTHONDONTWRITEBYTECODE=1 `
    -e GUARD_FUZZ_SCALE=$FuzzScale $Image sh -c "$install && python -m pytest -p no:cacheprovider -W ignore::UserWarning"
exit $LASTEXITCODE
