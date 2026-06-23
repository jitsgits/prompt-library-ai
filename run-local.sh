#!/bin/bash
set -e

echo "=== Creating Docker Network ==="
docker network create prompt-net 2>/dev/null || true

echo "=== Stopping & Removing Existing Containers ==="
docker stop prompt-ui prompt-be 2>/dev/null || true
docker rm prompt-ui prompt-be 2>/dev/null || true

echo "=== Building and Publishing Containers to Local Docker Daemon ==="
dotnet publish PromptBE/PromptBE.csproj -t:PublishContainer -c Release
dotnet publish PromptUI/PromptUI.csproj -t:PublishContainer -c Release

echo "=== Launching Backend Service (PromptBE) ==="
docker run -d \
  --name prompt-be \
  --network prompt-net \
  -p 5125:8080 \
  prompt-be:latest

echo "=== Launching Frontend UI (PromptUI) ==="
docker run -d \
  --name prompt-ui \
  --network prompt-net \
  -p 5173:8080 \
  -e BackendUrl=http://prompt-be:8080 \
  prompt-ui:latest

echo "=== Startup Complete! ==="
echo "Access the dashboard at: http://localhost:5173"
