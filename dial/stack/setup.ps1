<#
.SYNOPSIS
  Generates the local secrets of the DIAL stack (git-ignored): DIAL Core encryption values, one DIAL API key per
  identity, and the Core configuration that contains them.

.DESCRIPTION
  Writes
    .env                      DIAL_ENCRYPTION_SECRET and DIAL_ENCRYPTION_KEY for DIAL Core, and
                              CLAIMSASSIST_PSEUDONYM_KEY for run-claimsassist.ps1 (Dial mode refuses the public
                              development key); added to an existing .env without rotating anything else
    keys.json                 identity -> DIAL API key (used by the end-to-end tests and PilotGate's DIAL mode)
    config/core-config.json   config/core-config.base.json plus the generated "keys" section
    data/events/              where DIAL Guard writes its event log
  All values come from a cryptographic random generator. Nothing is written if the files exist, unless -Force.
  Note: in DIAL Core an application's "userRoles" decides who may call it; role "limits" only set quotas.

.PARAMETER RefreshConfig
  Rebuild config/core-config.json from the base file with the existing keys (after editing the base config).
  DIAL Core reloads the file within a few seconds; nothing is rotated.
#>
param([switch]$Force, [switch]$RefreshConfig)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$envFile = Join-Path $here ".env"
$keysFile = Join-Path $here "keys.json"
$configFile = Join-Path $here "config\core-config.json"
$baseFile = Join-Path $here "config\core-config.base.json"
$utf8 = New-Object System.Text.UTF8Encoding $false  # no BOM: DIAL Core and Python read these files

function New-Secret([int]$bytes = 32) {
    $buffer = New-Object byte[] $bytes
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
    -join ($buffer | ForEach-Object { $_.ToString("x2") })
}

New-Item -ItemType Directory -Force (Join-Path $here "data\events") | Out-Null

# identity => DIAL key settings (project, role). Roles are defined in config/core-config.base.json.
$identities = [ordered]@{
    "tester"        = @("pilotgate-tests", "tester")
    "anna.property" = @("anna.property", "claims-property")
    "ben.motor"     = @("ben.motor", "claims-motor")
    "clara.health"  = @("clara.health", "claims-health")
    "sam.siu"       = @("sam.siu", "siu")
    "intern"        = @("intern", "claims-intern")
}

function Write-CoreConfig([hashtable]$keys) {
    $entries = foreach ($name in $identities.Keys) {
        $project, $role = $identities[$name]
        "    `"$($keys[$name])`": { `"project`": `"$project`", `"role`": `"$role`" }"
    }
    $base = [IO.File]::ReadAllText($baseFile)
    if ($base -notmatch '"keys": \{\}') { throw "config/core-config.base.json must end with an empty `"keys`": {} section" }
    $config = $base -replace '"keys": \{\}', ("`"keys`": {`n" + ($entries -join ",`n") + "`n  }")
    [IO.File]::WriteAllText($configFile, $config, $utf8)
}

if ($RefreshConfig) {
    if (-not (Test-Path $keysFile)) { throw "keys.json not found: run setup.ps1 without -RefreshConfig first" }
    $existing = @{}
    (Get-Content $keysFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $existing[$_.Name] = $_.Value }
    foreach ($name in $identities.Keys) { if (-not $existing.ContainsKey($name)) { throw "keys.json has no key for $name; use -Force" } }
    Write-CoreConfig $existing
    Write-Host "Rebuilt config/core-config.json with the existing keys."
    return
}

function Add-MissingPseudonymKey {
    $text = if (Test-Path $envFile) { [IO.File]::ReadAllText($envFile) } else { "" }
    if ($text -match '(?m)^CLAIMSASSIST_PSEUDONYM_KEY=\S{32,}') { return }
    if ($text -match '(?m)^CLAIMSASSIST_PSEUDONYM_KEY=') { throw ".env has an unusable CLAIMSASSIST_PSEUDONYM_KEY (under 32 characters); remove that line and run setup.ps1 again" }
    $separator = if ($text.Length -gt 0 -and -not $text.EndsWith("`n")) { "`n" } else { "" }
    [IO.File]::AppendAllText($envFile, "${separator}CLAIMSASSIST_PSEUDONYM_KEY=$(New-Secret)`n", $utf8)
    Write-Host "Added CLAIMSASSIST_PSEUDONYM_KEY to .env."
}

if ((Test-Path $envFile) -and (Test-Path $keysFile) -and (Test-Path $configFile) -and -not $Force) {
    Add-MissingPseudonymKey
    Write-Host "Already set up. Use -Force to generate new keys (running containers must then be recreated)."
    return
}

$keys = @{}
foreach ($name in $identities.Keys) { $keys[$name] = "dial-" + (New-Secret 20) }
Write-CoreConfig $keys
$keyLines = foreach ($name in $identities.Keys) { "  `"$name`": `"$($keys[$name])`"" }
[IO.File]::WriteAllText($keysFile, "{`n" + ($keyLines -join ",`n") + "`n}`n", $utf8)
[IO.File]::WriteAllText($envFile, "DIAL_ENCRYPTION_SECRET=$(New-Secret)`nDIAL_ENCRYPTION_KEY=$(New-Secret)`nCLAIMSASSIST_PSEUDONYM_KEY=$(New-Secret)`n", $utf8)

Write-Host "Wrote .env, keys.json and config/core-config.json ($($identities.Count) DIAL keys)."
