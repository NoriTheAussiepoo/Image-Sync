$projectPath = Join-Path $PSScriptRoot "ImageSyncApp\ImageSyncApp.csproj"
$publishPath = Join-Path $PSScriptRoot "ImageSyncApp\bin\Release\net10.0-windows\win-x64\publish"

dotnet publish $projectPath --configuration Release --runtime win-x64 --self-contained false --output $publishPath
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Published Image Sync to $publishPath"
