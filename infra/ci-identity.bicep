// CI identity for the PilotGate pipeline: a user-assigned managed identity that may only call the model.
// The pipeline signs in through workload identity federation, so no client secret exists anywhere.
// It references the Azure OpenAI account from main.bicep as an existing resource and never modifies it.
//
// 1. az deployment group create -g <rg> -n pilotgate-ci -f infra/ci-identity.bicep -p accountName=<accountName output of main.bicep>
// 2. Create the Azure DevOps service connection (workload identity federation, manual) with the output ciClientId.
// 3. Run step 1 again with -p ciFederationIssuer=<issuer> ciFederationSubject=<subject> from that service connection.
targetScope = 'resourceGroup'

param location string = resourceGroup().location

@description('Name of the Azure OpenAI account created by main.bicep (its accountName output).')
param accountName string

@description('Issuer shown by the Azure DevOps workload identity service connection. Empty means no federated credential yet.')
param ciFederationIssuer string = ''

@description('Subject shown by the Azure DevOps workload identity service connection.')
param ciFederationSubject string = ''

resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: accountName
}

resource ciIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-pilotgate-ci'
  location: location
  tags: {
    project: 'PilotGate'
    purpose: 'ci-gate'
  }
}

resource ciFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = if (!empty(ciFederationIssuer) && !empty(ciFederationSubject)) {
  parent: ciIdentity
  name: 'azure-pipelines'
  properties: {
    issuer: ciFederationIssuer
    subject: ciFederationSubject
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

// Least privilege: data-plane inference on this one account. No subscription or resource group role.
var cognitiveServicesOpenAiUser = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

resource ciInferenceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, 'id-pilotgate-ci', cognitiveServicesOpenAiUser)
  scope: account
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAiUser)
    principalId: ciIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output ciClientId string = ciIdentity.properties.clientId
output ciTenantId string = subscription().tenantId
