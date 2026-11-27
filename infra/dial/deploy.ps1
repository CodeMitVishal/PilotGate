<#
.SYNOPSIS
  Deploys the DIAL suite (DIAL Core + Redis, DIAL Guard, ClaimsAssist, mock model) to Azure Container Apps, smoke-tests
  it, and can remove it again. Costs money: see infra/dial/README.md. Refuses to run without -ApproveCost.

.DESCRIPTION
  1. Phase 1 (deployApps=false): registry, identity, Log Analytics, Container Apps environment, role assignments.
  2. Images: imports DIAL Core and Redis into the registry by digest (no anonymous Docker Hub pulls at run time), then
     pushes the three images built locally. Both use your Entra sign-in; the registry has no admin user or password.
  3. Phase 2 (deployApps=true): the container apps. Secrets are generated once into git-ignored files next to this
     script and reused on every redeploy, so nothing rotates by accident:
       .keys.json     one DIAL API key per identity (PilotGate's DIAL mode and the smoke test read it)
       .secrets.json  DIAL Core's encryption secret and key, ClaimsAssist's audit pseudonymisation key
     No secret is printed.
  4. Smoke test through the public DIAL Core URL: health; DIAL Guard redacting PII on the way back from the mock
     model; one ClaimsAssist turn as a property handler (identity callback to Core, managed identity to Azure OpenAI);
     an approval proposed, decided once and run; an unmapped role refused with 403; the internal apps not reachable
     from the internet.

  -HighAvailability runs ClaimsAssist and DIAL Guard with at least two replicas and keeps pending approvals in Azure
  Managed Redis (Microsoft Entra ID only; ClaimsAssist's own identity). It registers the Microsoft.Cache resource
  provider if needed. DIAL Core stays at one replica.

  -Destroy deletes exactly the resources this deployment created, by name. It never uses Complete mode, which in this
  resource group would also delete the Azure OpenAI account. It works after a partial deployment too.
#>
param(
    [string]$ResourceGroup = "rg-pilotgate-demo",
    # Empty: read from the outputs of the deployment that created the account (infra/main.bicep).
    [string]$OpenAiAccountName = "",
    [string]$InfraDeployment = "pilotgate-infra",
    [string]$Prefix = "pgdial",
    [string]$ImageTag = "1.0.0",
    # Two or more replicas of ClaimsAssist and DIAL Guard, with pending approvals in Azure Managed Redis (costs extra).
    [switch]$HighAvailability,
    [switch]$ApproveCost,
    [switch]$Destroy
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$template = Join-Path $here "main.bicep"
$keysFile = Join-Path $here ".keys.json"
$secretsFile = Join-Path $here ".secrets.json"
$deployment = "pilotgate-dial"
$utf8 = New-Object Text.UTF8Encoding $false

function Invoke-Az {
    # Returns az's standard output as lines. On failure, throws with az's own error text (warnings dropped).
    # A plain function on purpose: a [Parameter()] attribute would add common parameters, and az's "-o" would then be
    # an ambiguous abbreviation of -OutVariable/-OutBuffer ("-d" would even bind to -Debug).
    $ErrorActionPreference = "Continue"  # Windows PowerShell turns native stderr into terminating errors under Stop
    $all = & az @args 2>&1
    $code = $LASTEXITCODE
    $out = @($all | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
    if ($code -ne 0) {
        $err = @($all | Where-Object { $_ -is [Management.Automation.ErrorRecord] } | ForEach-Object { $_.Exception.Message } |
            Where-Object { $_ -and $_ -notmatch '^WARNING:' }) -join " "
        throw "az $(($args | Select-Object -First 3) -join ' ') failed (exit $code): $err"
    }
    return $out
}

function Get-AzOrNull {
    try { return Invoke-Az @args } catch { return $null }
}

function ConvertFrom-AzJson([string[]]$Lines) { ($Lines -join "`n") | ConvertFrom-Json }

function New-Secret([int]$Bytes = 32) {
    $buffer = New-Object byte[] $Bytes
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
    -join ($buffer | ForEach-Object { $_.ToString("x2") })
}

function Invoke-Dial([string]$DeploymentId, [string]$Key, [string]$Message) {
    $body = @{ messages = @(@{ role = "user"; content = $Message }) } | ConvertTo-Json -Depth 4
    try {
        $r = Invoke-WebRequest "$dialUrl/openai/deployments/$DeploymentId/chat/completions" -Method Post -Body $body `
            -ContentType "application/json" -Headers @{ "Api-Key" = $Key } -TimeoutSec 180 -UseBasicParsing
        return @{ Status = [int]$r.StatusCode; Content = (ConvertFrom-Json $r.Content).choices[0].message.content }
    }
    catch {
        $response = $_.Exception.Response
        if ($null -ne $response) { return @{ Status = [int]$response.StatusCode; Content = "" } }
        throw
    }
}

function Invoke-DialUntilReady([string]$DeploymentId, [string]$Key, [string]$Message, [int]$Minutes = 6) {
    # Right after a deployment, replicas start, the mock model scales from zero, ClaimsAssist embeds its knowledge base,
    # and a new role assignment can take minutes to reach Azure OpenAI. ClaimsAssist reports such failures as 500
    # (DIAL Core would hide a 502 body), so 429 and 5xx are retried until the deadline; any other status is final.
    $deadline = (Get-Date).AddMinutes($Minutes)
    while ($true) {
        $result = Invoke-Dial $DeploymentId $Key $Message
        if ($result.Status -notin 429, 500, 502, 503, 504 -or (Get-Date) -gt $deadline) { return $result }
        Write-Host "    $DeploymentId answered $($result.Status); retrying ..."
        Start-Sleep -Seconds 20
    }
}

function Resolve-OpenAiAccount([string]$Name) {
    if ($Name) { return $Name }
    $found = (Get-AzOrNull deployment group show -g $ResourceGroup -n $InfraDeployment --query properties.outputs.accountName.value -o tsv) -join ""
    if (-not $found) { throw "No Azure OpenAI account found in deployment '$InfraDeployment' of $($ResourceGroup): deploy infra/main.bicep first, or pass -OpenAiAccountName." }
    return $found
}

function Test-DockerImage([string]$Image) {
    $ErrorActionPreference = "Continue"  # see Invoke-Az
    docker image inspect $Image *> $null
    return $LASTEXITCODE -eq 0
}

function Test-Reachable([string]$Url) {
    try { Invoke-WebRequest $Url -UseBasicParsing -TimeoutSec 15 | Out-Null; return $true } catch { return $false }
}

if ($Destroy) {
    Write-Host "Deleting the DIAL suite resources (prefix '$Prefix') from $ResourceGroup ..."
    $ids = Invoke-Az resource list -g $ResourceGroup --query "[?starts_with(name, '$Prefix-') || contains(name, '-$Prefix-') || starts_with(name, 'cr$Prefix') || starts_with(name, 'acr$Prefix') || name == 'id-$Prefix-apps'].id" -o tsv
    $apps = @($ids | Where-Object { $_ -match "/containerApps/" })
    $environments = @($ids | Where-Object { $_ -match "/managedEnvironments/" })
    # A Redis database goes with its cluster: deleting it separately after the cluster would fail and stop the clean-up.
    $rest = @($ids | Where-Object { $_ -notmatch "/containerApps/|/managedEnvironments/|/accounts/|/redisEnterprise/[^/]+/databases/" })
    $identity = Get-AzOrNull identity show -g $ResourceGroup -n "id-$Prefix-apps" --query principalId -o tsv
    if ($identity) {
        foreach ($a in (Invoke-Az role assignment list --all --assignee $identity --query "[].id" -o tsv)) {
            Invoke-Az role assignment delete --ids $a | Out-Null
        }
    }
    foreach ($id in @($apps + $environments + $rest)) {
        Write-Host "  delete $($id -replace '^/subscriptions/[^/]+/resourceGroups/', '')"
        Invoke-Az resource delete --ids $id | Out-Null
    }
    Write-Host "Done. The Azure OpenAI account and the CI identity were not touched. The local secret files stay;"
    Write-Host "delete $keysFile and $secretsFile to rotate everything on the next deployment."
    return
}

if (-not $ApproveCost) {
    Write-Host "This deployment costs money: about USD 45-100 per month while it runs (Container Apps 40-95, registry 5," -ForegroundColor Yellow
    Write-Host "logs about 1, plus model tokens); under USD 5 for a one-day demo. See infra/dial/README.md." -ForegroundColor Yellow
    if ($HighAvailability) {
        Write-Host "-HighAvailability adds about USD 30-85 per month (a second ClaimsAssist and DIAL Guard replica, Azure Managed" -ForegroundColor Yellow
        Write-Host "Redis B0); about USD 1-3 more per demo day." -ForegroundColor Yellow
    }
    Write-Host "Run again with -ApproveCost to deploy, and with -Destroy to remove it."
    exit 2
}

$OpenAiAccountName = Resolve-OpenAiAccount $OpenAiAccountName
Write-Host "Azure OpenAI account: $OpenAiAccountName"

foreach ($image in "pilotgate/dial-guard:$ImageTag", "pilotgate/dial-mockmodel:$ImageTag", "pilotgate/claimsassist:$ImageTag") {
    if (-not (Test-DockerImage $image)) { throw "Image $image not found: build it first (docker compose build in dial/stack; docker build -f src/ClaimsAssist.Api/Dockerfile -t pilotgate/claimsassist:$ImageTag .)" }
}

if (-not (Test-Path $keysFile)) {
    $keys = [ordered]@{}
    foreach ($who in "tester", "anna.property", "ben.motor", "clara.health", "sam.siu", "intern") { $keys[$who] = "dial-" + (New-Secret 20) }
    [IO.File]::WriteAllText($keysFile, ($keys | ConvertTo-Json), $utf8)
}
if (-not (Test-Path $secretsFile)) {
    $secrets = [ordered]@{ encryptionSecret = (New-Secret); encryptionKey = (New-Secret); pseudonymKey = (New-Secret) }
    [IO.File]::WriteAllText($secretsFile, ($secrets | ConvertTo-Json), $utf8)
}
$keysObject = Get-Content $keysFile -Raw | ConvertFrom-Json
$secretsObject = Get-Content $secretsFile -Raw | ConvertFrom-Json
foreach ($name in "encryptionSecret", "encryptionKey", "pseudonymKey") {
    if ("$($secretsObject.$name)".Length -lt 32) { throw "$secretsFile has no usable $name (at least 32 characters); delete the file to generate new values" }
}

$ha = if ($HighAvailability) { "true" } else { "false" }
if ($HighAvailability) {
    # Azure Managed Redis needs the Microsoft.Cache resource provider in the subscription (not registered by default).
    $state = (Invoke-Az provider show -n Microsoft.Cache --query registrationState -o tsv) -join ""
    if ($state -ne "Registered") {
        Write-Host "Registering the Microsoft.Cache resource provider ..."
        Invoke-Az provider register -n Microsoft.Cache --wait | Out-Null
    }
}

Write-Host "Phase 1: registry, identity, logs, environment$(if ($HighAvailability) { ', approval store (Azure Managed Redis)' }) ..."
$phase1 = ConvertFrom-AzJson (Invoke-Az deployment group create -g $ResourceGroup -n "$deployment-base" --template-file $template `
        --parameters openAiAccountName=$OpenAiAccountName prefix=$Prefix deployApps=false highAvailability=$ha --query properties.outputs -o json)
$registry = $phase1.registryName.value
$server = $phase1.registryLoginServer.value

Write-Host "Importing DIAL Core and Redis into $server (by digest) ..."
$bicep = [IO.File]::ReadAllText($template)
$imported = @{}
foreach ($param in "coreImage", "redisImage") {
    # The pinned default in main.bicep is the single source: docker.io/<repo>:<tag>@sha256:<digest>
    $m = [regex]::Match($bicep, "(?m)^param $param string = 'docker\.io/(?<repo>[^:@']+):(?<tag>[^@']+)@(?<digest>sha256:[0-9a-f]{64})'")
    if (-not $m.Success) { throw "main.bicep: $param must default to docker.io/<repo>:<tag>@sha256:<digest>" }
    $repo, $tag, $digest = $m.Groups["repo"].Value, $m.Groups["tag"].Value, $m.Groups["digest"].Value
    Invoke-Az acr import -n $registry --source "docker.io/$repo@$digest" --image "${repo}:$tag" --force | Out-Null
    $imported[$param] = "$server/$repo@$digest"
}

Write-Host "Pushing images to $server ..."
Invoke-Az acr login -n $registry | Out-Null
foreach ($name in "dial-guard", "dial-mockmodel", "claimsassist") {
    docker tag "pilotgate/${name}:$ImageTag" "$server/${name}:$ImageTag"
    docker push "$server/${name}:$ImageTag" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker push $name failed" }
}

$parameters = Join-Path ([IO.Path]::GetTempPath()) ("dial-params-" + [guid]::NewGuid().ToString("n") + ".json")
try {
    $payload = @{ '$schema' = "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#"; contentVersion = "1.0.0.0"
        parameters = @{
            dialKeys = @{ value = $keysObject }; openAiAccountName = @{ value = $OpenAiAccountName }; prefix = @{ value = $Prefix }
            imageTag = @{ value = $ImageTag }; deployApps = @{ value = $true }; highAvailability = @{ value = [bool]$HighAvailability }
            coreImage = @{ value = $imported["coreImage"] }; redisImage = @{ value = $imported["redisImage"] }
            encryptionSecret = @{ value = $secretsObject.encryptionSecret }; encryptionKey = @{ value = $secretsObject.encryptionKey }
            pseudonymKey = @{ value = $secretsObject.pseudonymKey }
        } }
    [IO.File]::WriteAllText($parameters, ($payload | ConvertTo-Json -Depth 6), $utf8)
    Write-Host "Phase 2: container apps ..."
    $phase2 = ConvertFrom-AzJson (Invoke-Az deployment group create -g $ResourceGroup -n $deployment --template-file $template `
            --parameters "@$parameters" --query properties.outputs -o json)
}
finally {
    Remove-Item $parameters -ErrorAction SilentlyContinue -Confirm:$false
}

$dialUrl = $phase2.dialUrl.value
Write-Host "DIAL Core: $dialUrl"
if ($phase2.coreUrlForApps.value -ne $dialUrl) {
    throw "The guard and ClaimsAssist were given $($phase2.coreUrlForApps.value), but Core's address is $($dialUrl). Fix coreUrl in main.bicep."
}

Write-Host "Smoke test ..."
$healthy = $false
for ($i = 0; $i -lt 60 -and -not $healthy; $i++) {
    try { $healthy = (Invoke-WebRequest "$dialUrl/health" -UseBasicParsing -TimeoutSec 10).StatusCode -eq 200 } catch { }
    if (-not $healthy) { Start-Sleep -Seconds 10 }
}
if (-not $healthy) { throw "DIAL Core did not answer /health within 10 minutes: see the ca-$Prefix-core logs in the Log Analytics workspace" }
Write-Host "  DIAL Core is healthy"

$answer = Invoke-DialUntilReady "mock-echo" $keysObject.tester "!pii"
if ($answer.Status -ne 200 -or $answer.Content -ne "Contact [EMAIL] or IBAN [IBAN ending 3000] now.") {
    throw "Smoke test failed: DIAL Guard via the mock model answered $($answer.Status) '$($answer.Content)'"
}
Write-Host "  DIAL Guard redacts PII through DIAL Core: ok"

$turn = Invoke-DialUntilReady "claimsassist" $keysObject."anna.property" "Is storm damage to a garden shed covered under household contents insurance?" -Minutes 10
if ($turn.Status -ne 200 -or $turn.Content -notmatch "AI-generated") {
    throw "Smoke test failed: ClaimsAssist answered $($turn.Status) without the AI disclosure: see the ca-$Prefix-claimsassist logs"
}
Write-Host "  ClaimsAssist answers a property handler (identity from Core, model via managed identity): ok"

# The approval round trip: a proposal waits for approval, and the decision runs it once (with -HighAvailability the two
# requests may reach different replicas, which share the approval through Redis).
$proposal = Invoke-DialUntilReady "claimsassist" $keysObject."anna.property" "Propose a settlement of 3,390 EUR for claim CLM-2026-0142, based on the contractor invoice."
$approvalId = [regex]::Match("$($proposal.Content)", 'approve (apr_[a-z]{12})').Groups[1].Value
if ($proposal.Status -ne 200 -or -not $approvalId) { throw "Smoke test failed: the proposal got $($proposal.Status) without an approval request" }
$decision = Invoke-Dial "claimsassist" $keysObject."anna.property" "approve $approvalId"
if ($decision.Status -ne 200 -or $decision.Content -notmatch "QUEUED") { throw "Smoke test failed: approving $approvalId answered $($decision.Status) without QUEUED" }
$again = Invoke-Dial "claimsassist" $keysObject."anna.property" "approve $approvalId"
if ($again.Content -notmatch "There is no pending approval") { throw "Smoke test failed: $approvalId could be decided twice" }
Write-Host "  an approval runs once, after the decision: ok"
if ($HighAvailability) {
    $replicas = [int]((Invoke-Az containerapp replica list -g $ResourceGroup -n "ca-$Prefix-claimsassist" --query "length(@)" -o tsv) -join "")
    if ($replicas -lt 2) { throw "Smoke test failed: ClaimsAssist runs $replicas replica(s); -HighAvailability needs at least 2" }
    Write-Host "  ClaimsAssist runs $replicas replicas: ok"
}

$denied = Invoke-DialUntilReady "claimsassist" $keysObject.intern "Show me claim CLM-2026-0142."
if ($denied.Status -ne 403) { throw "Smoke test failed: a role without a persona got $($denied.Status), expected 403" }
Write-Host "  a role without a persona is refused (403): ok"

foreach ($app in "ca-$Prefix-guard", "ca-$Prefix-claimsassist") {
    $fqdn = (Invoke-Az containerapp show -g $ResourceGroup -n $app --query properties.configuration.ingress.fqdn -o tsv) -join ""
    if (Test-Reachable "https://$fqdn/health") { throw "$app answered from the internet: its ingress must be internal" }
}
Write-Host "  the guard and ClaimsAssist are not reachable from the internet: ok"
Write-Host "Deployed. Keys: $keysFile and $secretsFile (git-ignored). Remove everything with: ./deploy.ps1 -Destroy"
