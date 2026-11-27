// The DIAL suite on Azure Container Apps: EPAM DIAL Core (+ Redis sidecar), DIAL Guard, ClaimsAssist as a DIAL
// application, and the mock model. Deployed into the existing resource group next to the Azure OpenAI account, which it
// references and never changes (infra/main.bicep is never redeployed: Azure's anti-abuse check 715-123420).
//
// Network: DIAL Core is the only app with external (HTTPS) ingress. The guard, ClaimsAssist and the mock model have
// internal-only ingress, so nobody outside the environment can call an application directly and skip the guard.
// Identity: one user-assigned identity pulls images (AcrPull) and calls Azure OpenAI with Microsoft Entra ID
// (Cognitive Services OpenAI User). No registry passwords, no model keys; logs go to Log Analytics through
// diagnostic settings, without a workspace key.
//
// Two phases (deploy.ps1): deployApps=false creates the registry, identity, logs and environment; after the images are
// built into the registry, deployApps=true adds the container apps.

targetScope = 'resourceGroup'

@description('Region of the new resources (the existing Azure OpenAI account stays where it is).')
param location string = resourceGroup().location

@description('Short prefix for resource names.')
@minLength(3)
@maxLength(12)
param prefix string = 'pgdial'

@description('Existing Azure OpenAI account in this resource group (Entra-only, local auth disabled).')
param openAiAccountName string

param chatDeployment string = 'gpt-4.1-mini'

param embeddingDeployment string = 'text-embedding-3-small'

@description('False: registry, identity, logs and environment only. True: also the container apps (images must exist).')
param deployApps bool = false

@description('Image tag the build pushed for dial-guard, dial-mockmodel and claimsassist.')
param imageTag string = '1.0.0'

@description('DIAL Core image, pinned by digest. deploy.ps1 passes the copy it imported into the registry (same digest).')
param coreImage string = 'docker.io/epam/ai-dial-core:0.50.8@sha256:2c5b3a7e2fa7b447a6e0d22dc98b2980f340d6cb5aa6c2fb4b99179b02810934'

@description('Redis image, pinned by digest. deploy.ps1 passes the copy it imported into the registry (same digest).')
param redisImage string = 'docker.io/library/redis:7.2.4-alpine3.19@sha256:c8bb255c3559b3e458766db810aa7b3c7af1235b204cfdb304e79ff388fe1a5a'

@secure()
@description('DIAL Core storage encryption secret (random, at least 32 characters).')
param encryptionSecret string = newGuid()

@secure()
@description('DIAL Core storage encryption key (random, at least 32 characters).')
param encryptionKey string = newGuid()

@secure()
@description('DIAL API keys: {"tester": "...", "anna.property": "...", "ben.motor": "...", "clara.health": "...", "sam.siu": "...", "intern": "..."}')
param dialKeys object = {}

@secure()
@description('ClaimsAssist audit pseudonymisation key (HMAC; random, at least 32 characters). deploy.ps1 keeps one stable value, so pseudonyms stay comparable across revisions; ClaimsAssist refuses to start in Dial mode without it.')
param pseudonymKey string = newGuid()

@description('Hosts (and their subdomains) that answers may link to. DIAL Guard and ClaimsAssist both enforce this list.')
param allowedLinkHosts array = [ 'intranet.contoso.example' ]

@description('Groups that may decide on another handler\'s pending approval (four-eyes escalation). Empty: only the requesting handler decides.')
param approverGroups array = [ 'claims-teamlead' ]

@description('Optional: Microsoft Entra tenant id for DIAL user sign-in (with entraAudience). Empty: API keys only.')
param entraTenantId string = ''

@description('Optional: application (client) id that DIAL access tokens must be issued for.')
param entraAudience string = ''

@description('High availability for the apps in the request path: ClaimsAssist and DIAL Guard run at least two replicas, and ClaimsAssist keeps pending approvals in Azure Managed Redis (Entra ID only, access keys disabled), so any replica can decide them and a restart loses none. DIAL Core stays at one replica (docs/production-path.md). Costs extra: see infra/dial/README.md.')
param highAvailability bool = false

var suffix = uniqueString(resourceGroup().id, prefix)
var names = {
  logs: 'log-${prefix}-${suffix}'
  environment: 'cae-${prefix}-${suffix}'
  registry: take('cr${prefix}${suffix}', 50)
  identity: 'id-${prefix}-apps'
  redisIdentity: 'id-${prefix}-redis'
  redis: 'amr-${prefix}-${suffix}'
  core: 'ca-${prefix}-core'
  guard: 'ca-${prefix}-guard'
  claimsassist: 'ca-${prefix}-claimsassist'
  mockmodel: 'ca-${prefix}-mockmodel'
}

var openAiUserRole = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd' // Cognitive Services OpenAI User
var acrPullRole = '7f951dda-4ed3-4680-a7ca-43fe172d538d' // AcrPull

resource openAi 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: openAiAccountName
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: names.logs
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    features: { disableLocalAuth: true }
  }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: names.registry
  location: location
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
  }
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: names.identity
  location: location
}

resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, identity.id, acrPullRole)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRole)
  }
}

resource modelAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: openAi
  name: guid(openAi.id, identity.id, openAiUserRole)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', openAiUserRole)
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: names.environment
  location: location
  properties: {
    appLogsConfiguration: { destination: 'azure-monitor' }
    workloadProfiles: [ { name: 'Consumption', workloadProfileType: 'Consumption' } ]
    // TLS between the apps inside the environment (Core -> guard -> Core -> app): the hops carry the per-request DIAL
    // key and claim data. The apps still call each other by name over HTTP; the platform encrypts the hop.
    peerTrafficConfiguration: { encryption: { enabled: true } }
  }
}

resource environmentLogs 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: environment
  name: 'to-log-analytics'
  properties: {
    workspaceId: logs.id
    logs: [ { categoryGroup: 'allLogs', enabled: true } ]
  }
}

// ---- Approval store (highAvailability only) --------------------------------------------------------------------------
// Pending approvals in Azure Managed Redis, so every ClaimsAssist replica can decide them (one atomic GETDEL decides).
// Its own identity, attached to ClaimsAssist only: the shared app identity also runs DIAL Core, the one app on the
// internet, and a team lead's decision trusts the requester's authority stored in the record.

resource redisIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (highAvailability) {
  name: names.redisIdentity
  location: location
}

resource redis 'Microsoft.Cache/redisEnterprise@2025-07-01' = if (highAvailability) {
  name: names.redis
  location: location
  sku: { name: 'Balanced_B0' } // the smallest size; approvals are a few kilobytes each
  properties: {
    minimumTlsVersion: '1.2'
    highAvailability: 'Enabled' // replicated: losing a node loses no pending approval
    // The Container Apps environment has no virtual network, so the apps reach Redis on its public endpoint: TLS and
    // Microsoft Entra ID only, no access keys. Production: a private endpoint and a VNet-integrated environment.
    publicNetworkAccess: 'Enabled'
  }
}

resource redisDatabase 'Microsoft.Cache/redisEnterprise/databases@2025-07-01' = if (highAvailability) {
  parent: redis
  name: 'default'
  properties: {
    clientProtocol: 'Encrypted'
    port: 10000
    clusteringPolicy: 'EnterpriseCluster' // one endpoint: the client needs no cluster support
    // Not the default (VolatileLRU): every approval has a time to live, so a volatile policy would evict live approvals
    // under memory pressure. NoEviction refuses new writes instead, and ClaimsAssist then refuses the proposal.
    evictionPolicy: 'NoEviction'
    accessKeysAuthentication: 'Disabled' // Microsoft Entra ID only
  }
}

resource redisAccess 'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-07-01' = if (highAvailability) {
  parent: redisDatabase
  name: 'claimsassist'
  properties: {
    accessPolicyName: 'default'
    user: { objectId: redisIdentity!.properties.principalId }
  }
}

// ---- Addresses ------------------------------------------------------------------------------------------------------
// Container Apps redirects plain HTTP to HTTPS unless an ingress sets allowInsecure. So:
// * Core calls the internal apps by name over HTTP (http://<app>); their ingress is internal-only and allows HTTP,
//   which no client outside the environment can reach, and peer-to-peer encryption protects the hop.
// * The guard and ClaimsAssist call Core through its public HTTPS name. Core never accepts plain HTTP, because its
//   ingress is the one exposed to the internet.

var coreUrl = 'https://${names.core}.${environment.properties.defaultDomain}'
var internalIngress = { external: false, transport: 'http', allowInsecure: true }

// ---- DIAL Core configuration -------------------------------------------------------------------------------------

var guardEndpoint = 'http://${names.guard}/openai/deployments/dial-guard/chat/completions'
var policy = {
  allowed_link_hosts: allowedLinkHosts
  // A fixed test marker, on purpose: ClaimsAssist's prompt, the gate (gate/gate.json) and the guard must all know it.
  canaries: [ 'SYS-CANARY-9921' ]
  tool_names: [ 'propose_settlement', 'get_claim', 'search_knowledge' ]
}
var identities = {
  tester: { project: 'pilotgate-tests', role: 'tester' }
  'anna.property': { project: 'anna.property', role: 'claims-property' }
  'ben.motor': { project: 'ben.motor', role: 'claims-motor' }
  'clara.health': { project: 'clara.health', role: 'claims-health' }
  'sam.siu': { project: 'sam.siu', role: 'siu' }
  intern: { project: 'intern', role: 'claims-intern' }
}
var keySection = toObject(filter(items(identities), i => contains(dialKeys, i.key)), i => dialKeys[i.key], i => i.value)

var coreConfig = {
  applications: {
    'mock-echo': {
      endpoint: 'http://${names.mockmodel}/openai/deployments/mock-echo/chat/completions'
      displayName: 'Mock model behind DIAL Guard (strict profile)'
      interceptors: [ 'dial-guard-strict' ]
      userRoles: [ 'tester' ]
    }
    claimsassist: {
      endpoint: 'http://${names.claimsassist}/openai/deployments/claimsassist/chat/completions'
      displayName: 'ClaimsAssist'
      description: 'Claims assistant for Contoso Versicherung (synthetic data)'
      interceptors: [ 'dial-guard-claimsassist' ]
      userRoles: [ 'claims-property', 'claims-motor', 'claims-health', 'siu', 'claims-intern' ]
    }
  }
  interceptors: {
    'dial-guard-strict': {
      endpoint: guardEndpoint
      displayName: 'DIAL Guard (strict)'
      defaults: { custom_fields: { interceptor_configuration: union(policy, { profile: 'strict' }) } }
    }
    'dial-guard-claimsassist': {
      endpoint: guardEndpoint
      displayName: 'DIAL Guard (ClaimsAssist)'
      defaults: { custom_fields: { interceptor_configuration: union(policy, { profile: 'claimsassist' }) } }
    }
  }
  roles: {
    tester: { limits: { 'mock-echo': {} } }
    'claims-property': { limits: { claimsassist: {} } }
    'claims-motor': { limits: { claimsassist: {} } }
    'claims-health': { limits: { claimsassist: {} } }
    siu: { limits: { claimsassist: {} } }
    'claims-intern': { limits: { claimsassist: {} } }
  }
  keys: keySection
}

var entra = !empty(entraTenantId) && !empty(entraAudience)
var login = az.environment().authentication.loginEndpoint // https://login.microsoftonline.com/ in the public cloud
var coreSettings = {
  server: { port: 8080 }
  identityProviders: entra ? {
    entra: {
      jwksUrl: '${login}${entraTenantId}/discovery/v2.0/keys'
      issuerPattern: '^${replace(login, '.', '\\.')}${entraTenantId}/v2\\.0$'
      audience: entraAudience
      rolePath: 'roles'
      userIdPath: 'oid'
    }
  } : {
    'api-keys-only': {
      jwksUrl: 'http://127.0.0.1:9/no-jwks'
      rolePath: 'roles'
      issuerPattern: '^https://issuer\\.invalid/accept-no-tokens$'
      audience: 'accept-no-tokens'
    }
  }
}

var registryConfig = [ { server: registry.properties.loginServer, identity: identity.id } ]
var userIdentity = { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }

// ---- Container apps -----------------------------------------------------------------------------------------------

resource core 'Microsoft.App/containerApps@2024-03-01' = if (deployApps) {
  name: names.core
  location: location
  identity: userIdentity
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      ingress: { external: true, targetPort: 8080, transport: 'http', allowInsecure: false }
      // deploy.ps1 imports the Core and Redis images into the registry (by digest): anonymous Docker Hub pulls are
      // rate-limited per IP address, and the Consumption plan pulls from shared addresses.
      registries: registryConfig
      secrets: [
        // Container Apps secrets are write-only (a GET never returns them); core-config holds the @secure() DIAL keys.
        #disable-next-line use-secure-value-for-secure-inputs
        { name: 'core-settings', value: string(coreSettings) }
        #disable-next-line use-secure-value-for-secure-inputs
        { name: 'core-config', value: string(coreConfig) }
        { name: 'encryption-secret', value: encryptionSecret }
        { name: 'encryption-key', value: encryptionKey }
      ]
    }
    template: {
      // Single replica, also with highAvailability: Redis runs as an in-memory sidecar and DIAL Core's storage is not
      // shared, so Core is the remaining single point of failure (docs/production-path.md).
      scale: { minReplicas: 1, maxReplicas: 1 }
      volumes: [
        {
          name: 'dial-config'
          storageType: 'Secret'
          secrets: [
            { secretRef: 'core-settings', path: 'settings.json' }
            { secretRef: 'core-config', path: 'core-config.json' }
          ]
        }
      ]
      containers: [
        {
          name: 'core'
          image: coreImage
          resources: { cpu: json('0.75'), memory: '1.5Gi' }
          env: [
            { name: 'JAVA_OPTS', value: '-Xms256m -Xmx768m -XX:+UseSerialGC' }
            { name: 'AIDIAL_SETTINGS', value: '/opt/dial/settings.json' }
            { name: 'aidial.config.files', value: '["/opt/dial/core-config.json"]' }
            { name: 'aidial.config.reload', value: '60000' }
            { name: 'aidial.redis.singleServerConfig.address', value: 'redis://localhost:6379' }
            { name: 'aidial.encryption.secret', secretRef: 'encryption-secret' }
            { name: 'aidial.encryption.key', secretRef: 'encryption-key' }
          ]
          volumeMounts: [ { volumeName: 'dial-config', mountPath: '/opt/dial' } ]
          probes: [
            { type: 'Startup', httpGet: { path: '/health', port: 8080 }, periodSeconds: 5, failureThreshold: 30 }
            { type: 'Liveness', httpGet: { path: '/health', port: 8080 }, periodSeconds: 30 }
          ]
        }
        {
          name: 'redis'
          image: redisImage
          args: [ '--maxmemory', '128mb', '--maxmemory-policy', 'volatile-lfu', '--save', '', '--appendonly', 'no', '--bind', '127.0.0.1' ]
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
        }
      ]
    }
  }
  dependsOn: [ pull ]
}

resource guard 'Microsoft.App/containerApps@2024-03-01' = if (deployApps) {
  name: names.guard
  location: location
  identity: userIdentity
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      ingress: union(internalIngress, { targetPort: 8000 })
      registries: registryConfig
    }
    template: {
      scale: {
        minReplicas: highAvailability ? 2 : 1
        maxReplicas: 3
        rules: [ { name: 'http', http: { metadata: { concurrentRequests: '50' } } } ]
      }
      containers: [
        {
          name: 'guard'
          image: '${registry.properties.loginServer}/dial-guard:${imageTag}'
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'DIAL_URL', value: coreUrl }
            { name: 'DIAL_GUARD_EVENTS_STDOUT', value: 'true' }
          ]
          probes: [
            { type: 'Readiness', httpGet: { path: '/health', port: 8000 }, periodSeconds: 10 }
            { type: 'Liveness', httpGet: { path: '/health', port: 8000 }, periodSeconds: 30 }
          ]
        }
      ]
    }
  }
  dependsOn: [ pull ]
}

// Replicas and the approval store switch together, so they can never disagree: more than one replica only with Redis.
var approvalStoreEnv = highAvailability ? [
  { name: 'ClaimsAssist__ApprovalStore', value: 'Redis' }
  { name: 'ClaimsAssist__Redis__ConnectionString', value: '${redis!.properties.hostName}:10000,ssl=true' }
  { name: 'ClaimsAssist__Redis__ManagedIdentityClientId', value: redisIdentity!.properties.clientId }
] : [
  { name: 'ClaimsAssist__ApprovalStore', value: 'Memory' }
]

resource claimsassist 'Microsoft.App/containerApps@2024-03-01' = if (deployApps) {
  name: names.claimsassist
  location: location
  identity: highAvailability
    ? { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {}, '${redisIdentity!.id}': {} } }
    : userIdentity
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      ingress: union(internalIngress, { targetPort: 8080 })
      registries: registryConfig
      secrets: [ { name: 'pseudonym-key', value: pseudonymKey } ]
    }
    template: {
      // One replica keeps pending approvals in memory; highAvailability runs two or more with the approvals in Redis.
      scale: highAvailability ? {
        minReplicas: 2
        maxReplicas: 4
        rules: [ { name: 'http', http: { metadata: { concurrentRequests: '20' } } } ]
      } : { minReplicas: 1, maxReplicas: 1 }
      containers: [
        {
          name: 'claimsassist'
          image: '${registry.properties.loginServer}/claimsassist:${imageTag}'
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat([
            { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
            { name: 'ClaimsAssist__Endpoint', value: openAi.properties.endpoint }
            { name: 'ClaimsAssist__ChatDeployment', value: chatDeployment }
            { name: 'ClaimsAssist__EmbeddingDeployment', value: embeddingDeployment }
            { name: 'ClaimsAssist__AuthMode', value: 'Dial' }
            { name: 'ClaimsAssist__PseudonymKey', secretRef: 'pseudonym-key' }
            // The replica's disk is lost on every restart; standard output reaches the Log Analytics workspace.
            { name: 'ClaimsAssist__AuditSink', value: 'Stdout' }
            { name: 'ClaimsAssist__Dial__Url', value: coreUrl }
            { name: 'ClaimsAssist__Dial__DeploymentId', value: 'claimsassist' }
            { name: 'ClaimsAssist__Dial__RolePersonas__claims-property', value: 'anna.property' }
            { name: 'ClaimsAssist__Dial__RolePersonas__claims-motor', value: 'ben.motor' }
            { name: 'ClaimsAssist__Dial__RolePersonas__claims-health', value: 'clara.health' }
            { name: 'ClaimsAssist__Dial__RolePersonas__siu', value: 'sam.siu' }
          ],
          // One link policy for DIAL Guard and ClaimsAssist: both read the same list.
          map(range(0, length(policy.allowed_link_hosts)), i => { name: 'ClaimsAssist__AllowedLinkHosts__${i}', value: policy.allowed_link_hosts[i] }),
          map(range(0, length(approverGroups)), i => { name: 'ClaimsAssist__ApproverGroups__${i}', value: approverGroups[i] }),
          approvalStoreEnv)
          // The port opens only after startup (index loaded, approval store connected). Readiness checks local state
          // only, so an outage of Redis or the model never takes every replica out of rotation at once.
          probes: [
            { type: 'Startup', httpGet: { path: '/health/live', port: 8080 }, periodSeconds: 5, failureThreshold: 36 }
            { type: 'Liveness', httpGet: { path: '/health/live', port: 8080 }, periodSeconds: 30 }
            { type: 'Readiness', httpGet: { path: '/health/ready', port: 8080 }, periodSeconds: 10 }
          ]
        }
      ]
    }
  }
  dependsOn: [ pull, modelAccess, redisAccess ]
}

resource mockmodel 'Microsoft.App/containerApps@2024-03-01' = if (deployApps) {
  name: names.mockmodel
  location: location
  identity: userIdentity
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      ingress: union(internalIngress, { targetPort: 7001 })
      registries: registryConfig
    }
    template: {
      scale: { minReplicas: 0, maxReplicas: 1 }
      containers: [
        {
          name: 'mockmodel'
          image: '${registry.properties.loginServer}/dial-mockmodel:${imageTag}'
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
        }
      ]
    }
  }
  dependsOn: [ pull ]
}

output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output identityClientId string = identity.properties.clientId
output dialUrl string = deployApps ? 'https://${core!.properties.configuration.ingress.fqdn}' : ''
@description('The DIAL URL the guard and ClaimsAssist were given; deploy.ps1 checks that it equals dialUrl.')
output coreUrlForApps string = coreUrl
