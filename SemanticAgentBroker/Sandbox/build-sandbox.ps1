# Build the AI Sandbox Python image
$ErrorActionPreference = 'Stop'

Write-Host "Building ai-sandbox-python:latest..."
docker build -t ai-sandbox-python:latest .
if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker build failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

Write-Host "Done. Verifying..."
docker run --rm --network none ai-sandbox-python:latest "print('Sandbox OK - Python ready')"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Sandbox verification failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}
