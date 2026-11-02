param([switch]$SkipTests)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectXml=[xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src/Kooky.App/Kooky.App.csproj') -Raw)
$appVersion=[string]$projectXml.Project.PropertyGroup.Version
Push-Location $projectRoot
try {
  Push-Location web
  try { npm ci; if($LASTEXITCODE){throw 'npm ci failed'}; npm run build; if($LASTEXITCODE){throw 'UI build failed'}; if(!$SkipTests){npm test;if($LASTEXITCODE){throw 'UI tests failed'}} } finally {Pop-Location}
  if(!$SkipTests){
    dotnet run --project tests/Kooky.ConPty.Tests/Kooky.ConPty.Tests.csproj -c Release
    if($LASTEXITCODE){throw 'ConPTY tests failed'}
    dotnet run --project tests/Kooky.Services.Tests/Kooky.Services.Tests.csproj -c Release
    if($LASTEXITCODE){throw 'Service tests failed'}
  }
  $buildStamp=[DateTime]::Now.ToString('yyyyMMddHHmmssfff')
  $publishStage=[IO.Path]::GetFullPath((Join-Path $projectRoot "artifacts/publish-$buildStamp"))
  dotnet publish src/Kooky.App/Kooky.App.csproj -c Release -o $publishStage --self-contained true
  if($LASTEXITCODE){throw 'Publish failed'}
  if(!$SkipTests){
    dotnet run --project tests/Kooky.Broker.Tests/Kooky.Broker.Tests.csproj -c Release -- --app-dir $publishStage
    if($LASTEXITCODE){throw 'Release broker integration tests failed'}
  }
  $publishTarget=[IO.Path]::GetFullPath((Join-Path $projectRoot 'dist/app'))
  $previousBuild=[IO.Path]::GetFullPath((Join-Path $projectRoot "artifacts/previous-app-$buildStamp"))
  $requiredPrefix=$projectRoot.TrimEnd('\')+'\'
  foreach($checkedPath in @($publishStage,$publishTarget,$previousBuild)){if(!$checkedPath.StartsWith($requiredPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Build path escaped project root'}}
  New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'dist') | Out-Null
  if(Test-Path -LiteralPath $publishTarget){Move-Item -LiteralPath $publishTarget -Destination $previousBuild}
  Move-Item -LiteralPath $publishStage -Destination $publishTarget
  dotnet tool restore
  dotnet wix build installer/Kooky.wxs -arch x64 -d "PublishDir=$projectRoot\dist\app" -d "AppVersion=$appVersion" -out "dist/Kooky-$appVersion-win-x64.msi" -pdbtype none
  if($LASTEXITCODE){throw 'MSI build failed'}
  $installer=Join-Path $projectRoot "dist/Kooky-$appVersion-win-x64.msi"
  @{version=$appVersion;url=$installer;sha256=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash;notes="Kooky for Windows $appVersion"} | ConvertTo-Json | Set-Content -LiteralPath dist/update-local.json -Encoding utf8
  Get-FileHash -LiteralPath $installer -Algorithm SHA256 | Format-List
} finally {Pop-Location}
