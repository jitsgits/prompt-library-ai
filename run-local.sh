#!/bin/bash
set -e

# Fetch connection string from Azure Container App secrets dynamically
echo "=== Fetching database connection string from Azure ==="
CONN_STR=$(az containerapp secret list --name "prompt-be" --resource-group "prompt-library-ai" --show-values --query "[?name=='db-connection-string'].value" --output tsv 2>/dev/null || true)

if [ -z "$CONN_STR" ]; then
  echo "⚠️ Warning: Could not retrieve database connection string from Azure. Ensure you are logged in (az login)."
fi

# Fetch Search Service Credentials dynamically from Azure
echo "=== Fetching Search Service Credentials from Azure ==="
SEARCH_NAME=$(az search service list --resource-group "prompt-library-ai" --query "[0].name" --output tsv 2>/dev/null || true)
SEARCH_KEY=""
SEARCH_ENDPOINT=""
if [ -n "$SEARCH_NAME" ]; then
  SEARCH_KEY=$(az search admin-key show --service-name "$SEARCH_NAME" --resource-group "prompt-library-ai" --query "primaryKey" --output tsv 2>/dev/null || true)
  SEARCH_ENDPOINT="https://${SEARCH_NAME}.search.windows.net"
  echo "Search Service credentials successfully loaded."
else
  echo "⚠️ Warning: Could not retrieve search service credentials from Azure."
fi

echo "=== Creating Docker Network ==="
docker network create prompt-net 2>/dev/null || true

echo "=== Stopping & Removing Existing Containers ==="
docker stop prompt-ui prompt-be prompt-vector-ingestion prompt-chatbot 2>/dev/null || true
docker rm prompt-ui prompt-be prompt-vector-ingestion prompt-chatbot 2>/dev/null || true

echo "=== Building and Publishing Containers to Local Docker Daemon ==="
dotnet publish PromptBE/PromptBE.csproj -t:PublishContainer -c Release
dotnet publish PromptUI/PromptUI.csproj -t:PublishContainer -c Release
dotnet publish PromptVectorIngestion/PromptVectorIngestion.csproj -t:PublishContainer -c Release
dotnet publish PromptChatbot/PromptChatbot.csproj -t:PublishContainer -c Release

echo "=== Launching Backend Service (PromptBE) ==="
docker run -d \
  --name prompt-be \
  --network prompt-net \
  -p 5125:8080 \
  -e ConnectionStrings__DefaultConnection="$CONN_STR" \
  prompt-be:latest

echo "=== Launching Ingestion Service (PromptVectorIngestion) ==="
docker run -d \
  --name prompt-vector-ingestion \
  --network prompt-net \
  -p 5130:8080 \
  -e SearchService__Endpoint="$SEARCH_ENDPOINT" \
  -e SearchService__ApiKey="$SEARCH_KEY" \
  -e SearchService__IndexName="prompts-index" \
  prompt-vector-ingestion:latest

echo "=== Resolving Local GitHub Models PAT ==="
LOCAL_GITHUB_PAT=""
LOCAL_OPENAI_ENDPOINT=""
if [ -f ".github_models_pat" ]; then
  LOCAL_GITHUB_PAT=$(cat .github_models_pat | tr -d '\r\n ')
  LOCAL_OPENAI_ENDPOINT="https://models.inference.ai.azure.com"
  echo "GitHub Models PAT loaded for local execution."
else
  echo "⚠️ Warning: .github_models_pat not found. Local Chatbot will use simulated response."
fi

echo "=== Launching Chatbot Service (PromptChatbot) ==="
docker run -d \
  --name prompt-chatbot \
  --network prompt-net \
  -p 5135:8080 \
  -e SearchService__Endpoint="$SEARCH_ENDPOINT" \
  -e SearchService__ApiKey="$SEARCH_KEY" \
  -e SearchService__IndexName="prompts-index" \
  -e AzureOpenAI__Endpoint="$LOCAL_OPENAI_ENDPOINT" \
  -e AzureOpenAI__ApiKey="$LOCAL_GITHUB_PAT" \
  -e AzureOpenAI__DeploymentName="gpt-4o-mini" \
  prompt-chatbot:latest

echo "=== Launching Frontend UI (PromptUI) ==="
docker run -d \
  --name prompt-ui \
  --network prompt-net \
  -p 5173:8080 \
  -e BackendUrl=http://prompt-be:8080 \
  -e ChatbotUrl=http://prompt-chatbot:8080 \
  prompt-ui:latest

echo "=== Startup Complete! ==="
echo "Access the dashboard at: http://localhost:5173"
