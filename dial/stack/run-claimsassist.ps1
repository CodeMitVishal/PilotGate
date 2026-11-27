<#
.SYNOPSIS
  Runs ClaimsAssist as an EPAM DIAL application on this machine, for the local DIAL stack.

.DESCRIPTION
  ClaimsAssist keeps its Entra sign-in (az login) and model access on the host; DIAL Core reaches it through
  host.docker.internal:5280 (config/core-config.base.json). It listens on 127.0.0.1 only. Identity comes only from DIAL
  Core (/v1/user/info); the development endpoints (/chat, /approvals, X-Dev-Persona) do not exist in this mode.

  Local trust boundary: any process on this machine that holds a static persona key from keys.json could call
  127.0.0.1:5280 directly and skip DIAL Guard. In Azure the application gets internal-only ingress (infra/dial).
#>
param(
    [string]$ResourceGroup = "rg-pilotgate-demo",
    [string]$Deployment = "pilotgate-infra",
    [int]$Port = 5280,
    [string]$DialUrl = "http://localhost:8080"
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")

$busy = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($busy) { Write-Host "Port $Port is already in use (process $($busy[0].OwningProcess))." -ForegroundColor Red; exit 2 }

# Dial mode refuses the public development pseudonym key; setup.ps1 keeps a random one in the git-ignored .env.
$envFile = Join-Path $PSScriptRoot ".env"
$keyLine = if (Test-Path $envFile) { Get-Content $envFile | Where-Object { $_ -like "CLAIMSASSIST_PSEUDONYM_KEY=*" } | Select-Object -First 1 }
if (-not $keyLine) { Write-Host "No CLAIMSASSIST_PSEUDONYM_KEY in dial/stack/.env: run ./setup.ps1 first." -ForegroundColor Red; exit 2 }
$env:ClaimsAssist__PseudonymKey = $keyLine.Substring("CLAIMSASSIST_PSEUDONYM_KEY=".Length).Trim()

if (-not $env:AZURE_OPENAI_ENDPOINT) {
    $env:AZURE_OPENAI_ENDPOINT = az deployment group show -g $ResourceGroup -n $Deployment --query properties.outputs.endpoint.value -o tsv
}

$env:ClaimsAssist__Endpoint = $env:AZURE_OPENAI_ENDPOINT
$env:ClaimsAssist__AuthMode = "Dial"
$env:ClaimsAssist__Dial__Url = $DialUrl
$env:ClaimsAssist__Dial__DeploymentId = "claimsassist"
${env:ClaimsAssist__Dial__RolePersonas__claims-property} = "anna.property"
${env:ClaimsAssist__Dial__RolePersonas__claims-motor} = "ben.motor"
${env:ClaimsAssist__Dial__RolePersonas__claims-health} = "clara.health"
$env:ClaimsAssist__Dial__RolePersonas__siu = "sam.siu"
$env:ClaimsAssist__AuditLogPath = "logs/claimsassist-dial-audit.jsonl"
$env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
$env:DOTNET_gcServer = "0"

dotnet build (Join-Path $repo "src\ClaimsAssist.Api") -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "ClaimsAssist (DIAL mode) on http://127.0.0.1:$Port, identity from $DialUrl"
# The built DLL directly, as scripts/run-gate.ps1 does: no launch profile can override the URL or the environment.
Set-Location $repo
dotnet (Join-Path $repo "src\ClaimsAssist.Api\bin\Release\net10.0\ClaimsAssist.Api.dll") --urls "http://127.0.0.1:$Port"
