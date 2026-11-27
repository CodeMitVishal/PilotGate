<#
.SYNOPSIS
  Starts ClaimsAssist, runs PilotGate against it and stops the agent again. The exit code is the gate's:
  0 = GO or GO WITH CONDITIONS, 1 = NO-GO, 2 = error.

.EXAMPLE
  ./scripts/run-gate.ps1                          # full run with the LLM judge
  ./scripts/run-gate.ps1 -NoJudge -Only P01,P02   # quick deterministic subset
#>
param(
    [string]$ResourceGroup = "rg-pilotgate-demo",
    [string]$Deployment = "pilotgate-infra",
    [switch]$NoJudge,
    [string[]]$Only,
    [string]$Label = "local run",
    # CI passes its own port, so a ClaimsAssist you run on 5080 never collides with the build agent on the same PC.
    [int]$Port = 5080
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# Stop early if something already listens on the agent's port: the gate would otherwise test that process
# (for example a ClaimsAssist started by hand) instead of the build this script makes.
$busy = $null
try { $busy = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop | Select-Object -First 1 } catch { }
if ($busy) {
    $owner = (Get-Process -Id $busy.OwningProcess -ErrorAction SilentlyContinue).ProcessName
    Write-Host "Port $Port is already in use by '$owner' (PID $($busy.OwningProcess)), probably a ClaimsAssist you started yourself. Stop it with Ctrl+C in its terminal, or pass another -Port, so the gate tests this build." -ForegroundColor Red
    exit 2
}

if (-not $env:AZURE_OPENAI_ENDPOINT) {
    $env:AZURE_OPENAI_ENDPOINT = az deployment group show -g $ResourceGroup -n $Deployment --query properties.outputs.endpoint.value -o tsv
}
$env:ClaimsAssist__Endpoint = $env:AZURE_OPENAI_ENDPOINT
# The development API (X-Dev-Persona) is opt-in and runs only on loopback; the gate binds to localhost below.
$env:ClaimsAssist__AuthMode = "DevPersonas"
$env:PILOTGATE_JUDGE_ENDPOINT = $env:AZURE_OPENAI_ENDPOINT

dotnet build -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { exit 2 }

# Free memory before anything is measured: build servers (MSBuild nodes, the C# compiler server) stay resident
# after a build, and on a small or busy machine they compete with the agent and inflate the latency the gate measures.
dotnet build-server shutdown | Out-Null
# Workstation GC keeps the agent's and the gate's memory small; server GC allocates one heap per core.
$env:DOTNET_gcServer = "0"

New-Item -ItemType Directory -Force -Path logs | Out-Null
$startArgs = @{
    FilePath               = "dotnet"
    ArgumentList           = "src/ClaimsAssist.Api/bin/Release/net10.0/ClaimsAssist.Api.dll --urls http://localhost:$Port"
    RedirectStandardOutput = "logs/agent.out.log"
    RedirectStandardError  = "logs/agent.err.log"
    PassThru               = $true
}
# On Windows, keep the agent's console hidden: an empty console window on the desktop invites closing it,
# which stops the agent in the middle of a gate run.
if ($env:OS -eq "Windows_NT") { $startArgs.WindowStyle = "Hidden" }
$agent = Start-Process @startArgs
$null = $agent.Handle # keep the handle, so the exit code is readable if the agent stops early

$code = 2
try {
    $ready = $false
    for ($i = 0; $i -lt 90 -and -not $ready; $i++) {
        try { Invoke-RestMethod "http://localhost:$Port/health" -TimeoutSec 2 | Out-Null; $ready = $true }
        catch { Start-Sleep -Seconds 1 }
    }
    if (-not $ready) { throw "ClaimsAssist did not become healthy; see logs/agent.err.log" }

    $gateArgs = @("run", "--project", "src/PilotGate", "-c", "Release", "--no-build", "--", "run", "--config", "gate/gate.json", "--agent", "http://localhost:$Port", "--label", $Label)
    if ($NoJudge) { $gateArgs += "--no-judge" }
    if ($Only) { $gateArgs += @("--only", ($Only -join ",")) }
    dotnet @gateArgs
    $code = $LASTEXITCODE

    if ($agent.HasExited) {
        Write-Warning "ClaimsAssist stopped during the run (exit code $($agent.ExitCode)). Last lines of its logs:"
        Get-Content logs/agent.err.log, logs/agent.out.log -Tail 15 -ErrorAction SilentlyContinue
    }
}
finally {
    Stop-Process -Id $agent.Id -ErrorAction SilentlyContinue
}
exit $code
