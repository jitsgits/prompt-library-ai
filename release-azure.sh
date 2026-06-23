#!/bin/bash
# Exit on any failure
set -e

# Configuration
RG_NAME="prompt-library-ai"
LOCATION="eastus"
ACR_NAME="promptlibaiacr" # Change if this name is already taken
BICEP_FILE="prompt-library-ai.bicep"

echo "=================================================="
echo "🚀 Ensuring Resource Group Exists"
echo "=================================================="
if ! az group show --name "$RG_NAME" &>/dev/null; then
  echo "Resource group '$RG_NAME' not found. Creating in $LOCATION..."
  az group create --name "$RG_NAME" --location "$LOCATION"
else
  echo "Resource group '$RG_NAME' already exists."
fi

echo "=================================================="
echo "🔐 Resolving SQL Database Credentials securely"
echo "=================================================="
SQL_PASSWORD=""

# Check if SQL password exists in Container App secrets
if az containerapp secret show --name "prompt-be" --resource-group "$RG_NAME" --secret-name "db-connection-string" &>/dev/null; then
  echo "Existing database connection found. Fetching password..."
  CONN_STR=$(az containerapp secret list --name "prompt-be" --resource-group "$RG_NAME" --show-values --query "[?name=='db-connection-string'].value" --output tsv)
  if [[ $CONN_STR =~ Password=([^;]+) ]]; then
    SQL_PASSWORD="${BASH_REMATCH[1]}"
    echo "SQL password successfully retrieved from secrets."
  fi
fi

if [ -z "$SQL_PASSWORD" ]; then
  echo "No existing SQL password found. Generating new secure password..."
  # Generate random password and ensure SQL Server complexity requirements are met
  SQL_PASSWORD=$(openssl rand -base64 12 | tr -dc 'a-zA-Z0-9')
  SQL_PASSWORD="${SQL_PASSWORD}1!aA"
fi

echo "=================================================="
echo "🚀 Checking for Azure Container Registry (ACR)"
echo "=================================================="
# Check if ACR exists
if ! az acr show --name "$ACR_NAME" --resource-group "$RG_NAME" &>/dev/null; then
  echo "ACR not found. Initiating bootstrap deployment..."
  
  # Deploy Bicep with empty versionTag to create ACR first (using placeholders for container apps)
  az deployment group create \
    --resource-group "$RG_NAME" \
    --template-file "$BICEP_FILE" \
    --parameters acrName="$ACR_NAME" versionTag="" sqlAdminPassword="$SQL_PASSWORD"
fi

# Retrieve the login server
ACR_LOGIN_SERVER=$(az acr show --name "$ACR_NAME" --resource-group "$RG_NAME" --query loginServer --output tsv)
echo "ACR Login Server: $ACR_LOGIN_SERVER"

echo "=================================================="
echo "🔐 Logging in to Azure Container Registry"
echo "=================================================="
az acr login --name "$ACR_NAME"

echo "=================================================="
echo "📦 Generating Entity Framework Migrations"
echo "=================================================="
# Build first to ensure correct metadata compilation
dotnet build PromptBE/PromptBE.csproj

if [ ! -d "PromptBE/Migrations" ]; then
  echo "Creating new migration 'InitialCreate'..."
  dotnet ef migrations add InitialCreate --project PromptBE/PromptBE.csproj --startup-project PromptBE/PromptBE.csproj
else
  echo "Migrations already exist. Skipping creation."
fi

echo "=================================================="
echo "🔍 Checking for code changes in PromptBE or PromptUI"
echo "=================================================="
CHANGES_DETECTED=true

if [ -f ".last_version_tag" ]; then
  # Check if there are uncommitted changes or differences from HEAD in the project folders
  if [ -z "$(git status --porcelain -- PromptBE/ PromptUI/)" ] && git diff --quiet HEAD -- PromptBE/ PromptUI/; then
    CHANGES_DETECTED=false
  fi
fi

if [ "$CHANGES_DETECTED" = "true" ]; then
  # Generate version tag based on current timestamp
  VERSION_TAG=$(date +%Y%m%d%H%M%S)
  echo "Changes detected. Generated New Version Tag: $VERSION_TAG"

  echo "=================================================="
  echo "🔨 Building Local Container Images using SDK"
  echo "=================================================="
  dotnet publish PromptBE/PromptBE.csproj -t:PublishContainer -c Release
  dotnet publish PromptUI/PromptUI.csproj -t:PublishContainer -c Release

  echo "=================================================="
  echo "🏷️ Tagging Images with Version & latest"
  echo "=================================================="
  # Tag PromptBE
  docker tag prompt-be:latest "${ACR_LOGIN_SERVER}/prompt-be:${VERSION_TAG}"
  docker tag prompt-be:latest "${ACR_LOGIN_SERVER}/prompt-be:latest"

  # Tag PromptUI
  docker tag prompt-ui:latest "${ACR_LOGIN_SERVER}/prompt-ui:${VERSION_TAG}"
  docker tag prompt-ui:latest "${ACR_LOGIN_SERVER}/prompt-ui:latest"

  echo "=================================================="
  echo "📤 Pushing Container Images to ACR"
  echo "=================================================="
  docker push "${ACR_LOGIN_SERVER}/prompt-be:${VERSION_TAG}"
  docker push "${ACR_LOGIN_SERVER}/prompt-be:latest"

  docker push "${ACR_LOGIN_SERVER}/prompt-ui:${VERSION_TAG}"
  docker push "${ACR_LOGIN_SERVER}/prompt-ui:latest"

  # Cache the tag locally
  echo "$VERSION_TAG" > .last_version_tag
else
  VERSION_TAG=$(cat .last_version_tag)
  echo "No changes detected in PromptBE/ or PromptUI/ source code."
  echo "Skipping container rebuild & push. Reusing tag: $VERSION_TAG"
fi

echo "=================================================="
echo "🌍 Deploying Container Apps via Bicep"
echo "=================================================="
# Deploy resource-group scope template passing the new version tag
az deployment group create \
  --resource-group "$RG_NAME" \
  --template-file "$BICEP_FILE" \
  --parameters acrName="$ACR_NAME" versionTag="$VERSION_TAG" sqlAdminPassword="$SQL_PASSWORD"

echo "=================================================="
echo "🎉 Release Complete!"
echo "=================================================="
