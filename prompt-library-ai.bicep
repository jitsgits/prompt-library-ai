targetScope = 'resourceGroup'

param location string = resourceGroup().location
param acrName string = 'promptlibaiacr'
param versionTag string = ''

param logAnalyticsWorkspaceName string = 'prompt-library-la'
param containerAppsEnvName string = 'prompt-library-env'
param promptBeName string = 'prompt-be'
param promptUiName string = 'prompt-ui'
param promptVectorIngestionName string = 'prompt-vector-ingestion'
param promptChatbotName string = 'prompt-chatbot'


// SQL Database Server Configuration - Renamed to prompt-sql-srv to resolve metadata collisions
param sqlServerName string = 'prompt-sql-srv-${uniqueString(resourceGroup().id)}'
param sqlDatabaseName string = 'prompt-library'
param sqlAdminUsername string = 'sqladmin'
@secure()
param sqlAdminPassword string
@secure()
param githubModelsPat string = ''
param sqlLocation string = 'centralus'

var beImage = empty(versionTag)
  ? 'mcr.microsoft.com/dotnet/samples:aspnetapp'
  : '${acrName}.azurecr.io/prompt-be:${versionTag}'
var uiImage = empty(versionTag)
  ? 'mcr.microsoft.com/dotnet/samples:aspnetapp'
  : '${acrName}.azurecr.io/prompt-ui:${versionTag}'
var ingestionImage = empty(versionTag)
  ? 'mcr.microsoft.com/dotnet/samples:aspnetapp'
  : '${acrName}.azurecr.io/prompt-vector-ingestion:${versionTag}'
var chatbotImage = empty(versionTag)
  ? 'mcr.microsoft.com/dotnet/samples:aspnetapp'
  : '${acrName}.azurecr.io/prompt-chatbot:${versionTag}'

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: acrName
  location: resourceGroup().location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: true
  }
}

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource containerAppsEnv 'Microsoft.App/managedEnvironments@2023-05-01' = {
  name: containerAppsEnvName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsWorkspace.properties.customerId
        sharedKey: logAnalyticsWorkspace.listKeys().primarySharedKey
      }
    }
  }
}

// SQL Database Server Resource
resource sqlServer 'Microsoft.Sql/servers@2021-11-01' = {
  name: sqlServerName
  location: sqlLocation
  properties: {
    administratorLogin: sqlAdminUsername
    administratorLoginPassword: sqlAdminPassword
  }
}

// SQL Firewall Rule to Allow internal Azure traffic (like Container Apps)
resource sqlServerFirewallRule 'Microsoft.Sql/servers/firewallRules@2021-11-01' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// SQL Database Resource
resource sqlDatabase 'Microsoft.Sql/servers/databases@2021-11-01' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: sqlLocation
  sku: {
    name: 'Basic'
    tier: 'Basic'
  }
}

// Azure AI Search Service
resource searchService 'Microsoft.Search/searchServices@2023-11-01' = {
  name: 'prompt-search-${uniqueString(resourceGroup().id)}'
  location: location
  sku: {
    name: 'free'
  }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'default'
  }
}


// Azure Event Hubs Namespace
resource eventHubNamespace 'Microsoft.EventHub/namespaces@2021-11-01' = {
  name: 'prompt-evh-${uniqueString(resourceGroup().id)}'
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
    capacity: 1
  }
}

// Event Hub / Topic "prompts"
resource eventHub 'Microsoft.EventHub/namespaces/eventhubs@2021-11-01' = {
  parent: eventHubNamespace
  name: 'prompts'
  properties: {
    messageRetentionInDays: 1
    partitionCount: 2
  }
}

resource consumerGroup 'Microsoft.EventHub/namespaces/eventhubs/consumergroups@2021-11-01' = {
  parent: eventHub
  name: 'prompt-vector-ingestion'
}

resource eventHubAuthRule 'Microsoft.EventHub/namespaces/authorizationRules@2021-11-01' existing = {
  parent: eventHubNamespace
  name: 'RootManageSharedAccessKey'
}

// Storage Account for Dapr Checkpoints
resource storageAccount 'Microsoft.Storage/storageAccounts@2021-09-01' = {
  name: 'promptst${uniqueString(resourceGroup().id)}'
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2021-09-01' = {
  parent: storageAccount
  name: 'default'
}

resource storageContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2021-09-01' = {
  parent: blobService
  name: 'daprcheckpoints'
}

// Dapr Component resource in the Container App environment
resource daprPubSub 'Microsoft.App/managedEnvironments/daprComponents@2023-05-01' = {
  parent: containerAppsEnv
  name: 'pubsub'
  properties: {
    componentType: 'pubsub.azure.eventhubs'
    version: 'v1'
    metadata: [
      {
        name: 'connectionString'
        value: eventHubAuthRule.listKeys().primaryConnectionString
      }
      {
        name: 'storageAccountName'
        value: storageAccount.name
      }
      {
        name: 'storageAccountKey'
        value: storageAccount.listKeys().keys[0].value
      }
      {
        name: 'storageContainerName'
        value: 'daprcheckpoints'
      }
    ]
    scopes: [
      'prompt-be'
      'prompt-vector-ingestion'
    ]
  }
}

resource promptBeApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: promptBeName
  location: location
  properties: {
    managedEnvironmentId: containerAppsEnv.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        allowInsecure: false
        transport: 'auto'
      }
      registries: [
        {
          server: acr.properties.loginServer
          username: acr.name
          passwordSecretRef: 'acr-password'
        }
      ]
      secrets: [
        {
          name: 'acr-password'
          value: acr.listCredentials().passwords[0].value
        }
        {
          name: 'db-connection-string'
          value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${sqlDatabaseName};User ID=${sqlAdminUsername};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
        }
      ]
      dapr: {
        enabled: true
        appId: 'prompt-be'
        appPort: 8080
      }
    }
    template: {
      containers: [
        {
          name: promptBeName
          image: beImage
          env: [
            {
              name: 'ConnectionStrings__DefaultConnection'
              secretRef: 'db-connection-string'
            }
            {
              name: 'AllowedOrigins'
              value: 'https://${promptUiName}.${containerAppsEnv.properties.defaultDomain}'
            }
            {
              name: 'IngestionUrl'
              value: 'http://${promptVectorIngestionName}'
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
    }
  }
}

resource promptUiApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: promptUiName
  location: location
  properties: {
    managedEnvironmentId: containerAppsEnv.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        allowInsecure: false
        transport: 'auto'
      }
      registries: [
        {
          server: acr.properties.loginServer
          username: acr.name
          passwordSecretRef: 'acr-password'
        }
      ]
      secrets: [
        {
          name: 'acr-password'
          value: acr.listCredentials().passwords[0].value
        }
      ]
    }
    template: {
      containers: [
        {
          name: promptUiName
          image: uiImage
          env: [
            {
              name: 'BackendUrl'
              value: 'https://${promptBeApp.properties.configuration.ingress.fqdn}'
            }
            {
              name: 'ChatbotUrl'
              value: 'http://${promptChatbotName}'
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
    }
  }
}

resource promptVectorIngestionApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: promptVectorIngestionName
  location: location
  properties: {
    managedEnvironmentId: containerAppsEnv.id
    configuration: {
      ingress: {
        external: false
        targetPort: 8080
        allowInsecure: false
        transport: 'auto'
      }
      registries: [
        {
          server: acr.properties.loginServer
          username: acr.name
          passwordSecretRef: 'acr-password'
        }
      ]
      secrets: [
        {
          name: 'acr-password'
          value: acr.listCredentials().passwords[0].value
        }
        {
          name: 'search-api-key'
          value: searchService.listAdminKeys().primaryKey
        }
        {
          name: 'github-models-pat'
          value: githubModelsPat
        }
      ]
      dapr: {
        enabled: true
        appId: 'prompt-vector-ingestion'
        appPort: 8080
      }
    }
    template: {
      containers: [
        {
          name: promptVectorIngestionName
          image: ingestionImage
          env: [
            {
              name: 'SearchService__Endpoint'
              value: 'https://${searchService.name}.search.windows.net'
            }
            {
              name: 'SearchService__ApiKey'
              secretRef: 'search-api-key'
            }
            {
              name: 'SearchService__IndexName'
              value: 'prompts-index'
            }
            {
              name: 'AzureOpenAI__Endpoint'
              value: 'https://models.inference.ai.azure.com'
            }
            {
              name: 'AzureOpenAI__ApiKey'
              secretRef: 'github-models-pat'
            }
            {
              name: 'AzureOpenAI__EmbeddingDeploymentName'
              value: 'text-embedding-3-small'
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

resource promptChatbotApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: promptChatbotName
  location: location
  properties: {
    managedEnvironmentId: containerAppsEnv.id
    configuration: {
      ingress: {
        external: false
        targetPort: 8080
        allowInsecure: false
        transport: 'auto'
      }
      registries: [
        {
          server: acr.properties.loginServer
          username: acr.name
          passwordSecretRef: 'acr-password'
        }
      ]
      secrets: [
        {
          name: 'acr-password'
          value: acr.listCredentials().passwords[0].value
        }
        {
          name: 'search-api-key'
          value: searchService.listAdminKeys().primaryKey
        }
        {
          name: 'github-models-pat'
          value: githubModelsPat
        }
      ]
    }
    template: {
      containers: [
        {
          name: promptChatbotName
          image: chatbotImage
          env: [
            {
              name: 'SearchService__Endpoint'
              value: 'https://${searchService.name}.search.windows.net'
            }
            {
              name: 'SearchService__ApiKey'
              secretRef: 'search-api-key'
            }
            {
              name: 'SearchService__IndexName'
              value: 'prompts-index'
            }
            {
              name: 'AzureOpenAI__Endpoint'
              value: 'https://models.inference.ai.azure.com'
            }
            {
              name: 'AzureOpenAI__ApiKey'
              secretRef: 'github-models-pat'
            }
            {
              name: 'AzureOpenAI__DeploymentName'
              value: 'gpt-4o-mini'
            }
            {
              name: 'AzureOpenAI__EmbeddingDeploymentName'
              value: 'text-embedding-3-small'
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

output uiFqdn string = promptUiApp.properties.configuration.ingress.fqdn
output beFqdn string = promptBeApp.properties.configuration.ingress.fqdn
