targetScope = 'resourceGroup'

param location string = resourceGroup().location
param acrName string = 'promptlibaiacr'
param versionTag string = ''

param logAnalyticsWorkspaceName string = 'prompt-library-la'
param containerAppsEnvName string = 'prompt-library-env'
param promptBeName string = 'prompt-be'
param promptUiName string = 'prompt-ui'

// SQL Database Server Configuration - Renamed to prompt-sql-srv to resolve metadata collisions
param sqlServerName string = 'prompt-sql-srv-${uniqueString(resourceGroup().id)}'
param sqlDatabaseName string = 'prompt-library'
param sqlAdminUsername string = 'sqladmin'
@secure()
param sqlAdminPassword string
param sqlLocation string = 'centralus'

var beImage = empty(versionTag)
  ? 'mcr.microsoft.com/dotnet/samples:aspnetapp'
  : '${acrName}.azurecr.io/prompt-be:${versionTag}'
var uiImage = empty(versionTag)
  ? 'mcr.microsoft.com/dotnet/samples:aspnetapp'
  : '${acrName}.azurecr.io/prompt-ui:${versionTag}'

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

output uiFqdn string = promptUiApp.properties.configuration.ingress.fqdn
output beFqdn string = promptBeApp.properties.configuration.ingress.fqdn
