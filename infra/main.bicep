// PilotGate demo infrastructure: one Azure OpenAI account with EU Data Zone deployments.
// Deploy:  az deployment group create -g <rg> -f infra/main.bicep -p principalId=<your-object-id>
// Delete:  az group delete -n <rg> --yes --no-wait
targetScope = 'resourceGroup'

@description('Azure region. Sweden Central offers EU Data Zone deployments for all three models used here.')
param location string = resourceGroup().location

@description('Globally unique account name. Also the custom subdomain, which Entra ID authentication requires.')
param accountName string = 'oai-pilotgate-${uniqueString(resourceGroup().id)}'

@description('Object ID of the user or service principal that runs the agent and the gate.')
param principalId string

@allowed([
  'User'
  'ServicePrincipal'
])
param principalType string = 'User'

@description('Agent model: non-reasoning, fast first token, reliable tool calling.')
param chatModel object = {
  deployment: 'gpt-4.1-mini'
  name: 'gpt-4.1-mini'
  version: '2025-04-14'
  capacityK: 50
}

@description('Judge model for LLM-as-judge evaluations: a stronger model than the agent, to limit self-preference bias.')
param judgeModel object = {
  deployment: 'gpt-4.1'
  name: 'gpt-4.1'
  version: '2025-04-14'
  capacityK: 30
}

param embeddingModel object = {
  deployment: 'text-embedding-3-small'
  name: 'text-embedding-3-small'
  version: '1'
  capacityK: 30
}

resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: accountName
  location: location
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  tags: {
    project: 'PilotGate'
    purpose: 'demo'
  }
  properties: {
    customSubDomainName: accountName
    // Entra ID only: no API keys exist, so none can leak into code, pipelines or prompts.
    disableLocalAuth: true
    // Prototype setting. Production path: private endpoint and publicNetworkAccess 'Disabled'.
    publicNetworkAccess: 'Enabled'
  }
}

// DataZoneStandard keeps processing inside the EU data zone (GDPR, client data-residency clauses).
// Deployments on one account must be created one after another, hence the dependsOn chain.
resource chat 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: account
  name: chatModel.deployment
  sku: {
    name: 'DataZoneStandard'
    capacity: chatModel.capacityK
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: chatModel.name
      version: chatModel.version
    }
    // Pinned: a silent model upgrade must go through the gate first.
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}

resource judge 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: account
  name: judgeModel.deployment
  sku: {
    name: 'DataZoneStandard'
    capacity: judgeModel.capacityK
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: judgeModel.name
      version: judgeModel.version
    }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
  dependsOn: [
    chat
  ]
}

resource embedding 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: account
  name: embeddingModel.deployment
  sku: {
    name: 'DataZoneStandard'
    capacity: embeddingModel.capacityK
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: embeddingModel.name
      version: embeddingModel.version
    }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
  dependsOn: [
    judge
  ]
}

// Least privilege: data-plane inference only. No Contributor or Owner on the account.
var cognitiveServicesOpenAiUser = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

resource inferenceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, principalId, cognitiveServicesOpenAiUser)
  scope: account
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAiUser)
    principalId: principalId
    principalType: principalType
  }
}

output endpoint string = account.properties.endpoint
output accountName string = account.name
output chatDeployment string = chat.name
output judgeDeployment string = judge.name
output embeddingDeployment string = embedding.name
