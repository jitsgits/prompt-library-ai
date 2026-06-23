#!/bin/bash
set -e

# Fetch connection string from Azure Container App secrets dynamically
echo "=== Fetching database connection string from Azure ==="
CONN_STR=$(az containerapp secret list --name "prompt-be" --resource-group "prompt-library-ai" --show-values --query "[?name=='db-connection-string'].value" --output tsv 2>/dev/null || true)

if [ -z "$CONN_STR" ]; then
  echo "⚠️ Warning: Could not retrieve database connection string from Azure. Ensure you are logged in (az login)."
fi

echo "=== Creating Docker Network ==="
docker network create prompt-net 2>/dev/null || true

echo "=== Stopping & Removing Existing Containers ==="
docker stop prompt-ui prompt-be prompt-vector-ingestion 2>/dev/null || true
docker rm prompt-ui prompt-be prompt-vector-ingestion 2>/dev/null || true

echo "=== Building and Publishing Containers to Local Docker Daemon ==="
dotnet publish PromptBE/PromptBE.csproj -t:PublishContainer -c Release
dotnet publish PromptUI/PromptUI.csproj -t:PublishContainer -c Release
dotnet publish PromptVectorIngestion/PromptVectorIngestion.csproj -t:PublishContainer -c Release

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
  prompt-vector-ingestion:latest

echo "=== Launching Frontend UI (PromptUI) ==="
docker run -d \
  --name prompt-ui \
  --network prompt-net \
  -p 5173:8080 \
  -e BackendUrl=http://prompt-be:8080 \
  prompt-ui:latest

echo "=== Startup Complete! ==="
echo "Access the dashboard at: http://localhost:5173"
