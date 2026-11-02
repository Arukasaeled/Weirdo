param([switch]$SkipTests,[switch]$SkipFrontend,[switch]$SkipBroker)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$brokerProject=Join-Path $projectRoot 'src/Weirdo.Broker/Weirdo.Broker.csproj'
$appVersion=(Get-Content (Join-Path $projectRoot 'src-tauri/tauri.conf.json') -Raw|ConvertFrom-Json).version
$cargoBin=if($env:CARGO_HOME){Join-Path $env:CARGO_HOME 'bin'}else{Join-Path $env:USERPROFILE '.cargo/bin'}
if(Test-Path $cargoBin){$env:PATH=$cargoBin+';'+$env:PATH}
Push-Location $projectRoot
try {
  npm ci
  if($LASTEXITCODE){throw 'Desktop dependency restore failed'}
  if(!$SkipFrontend){
    Push-Location web
    try {
      npm ci
      if($LASTEXITCODE){throw 'Frontend dependency restore failed'}
      npm run build
      if($LASTEXITCODE){throw 'Frontend build failed'}
      if(!$SkipTests){npm test;if($LASTEXITCODE){throw 'Frontend tests failed'}}
    } finally {Pop-Location}
  }
  if(!$SkipTests){
    dotnet run --project tests/Weirdo.ConPty.Tests -c Release
    if($LASTEXITCODE){throw 'ConPTY tests failed'}
    dotnet run --project tests/Weirdo.Services.Tests -c Release
    if($LASTEXITCODE){throw 'Service tests failed'}
  }
  if(!$SkipBroker){
    $brokerStamp=[DateTime]::Now.ToString('yyyyMMddHHmmssfff')
    $brokerStage=Join-Path $projectRoot "artifacts/native-$brokerStamp"
    dotnet publish $brokerProject -c Release -r win-x64 -o $brokerStage -p:PublishAot=true --self-contained true
    if($LASTEXITCODE){throw 'Native broker build failed'}
    $brokerTarget=Join-Path $projectRoot 'dist/native-broker'
    $previousBroker=Join-Path $projectRoot "artifacts/previous-broker-$brokerStamp"
    foreach($checked in @($brokerStage,$brokerTarget,$previousBroker)){
      if(![IO.Path]::GetFullPath($checked).StartsWith($projectRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Broker build path escaped project root'}
    }
    if(Test-Path -LiteralPath $brokerTarget){Move-Item -LiteralPath $brokerTarget -Destination $previousBroker}
    Move-Item -LiteralPath $brokerStage -Destination $brokerTarget
  }
  if(!$SkipTests){
    dotnet run --project tests/Weirdo.Broker.Tests -c Release -- --app-dir (Join-Path $projectRoot 'dist/native-broker')
    if($LASTEXITCODE){throw 'Native broker integration tests failed'}
  }
  npm exec tauri build -- --bundles nsis
  if($LASTEXITCODE){throw 'Tauri release build failed'}
  $metadata=cargo metadata --manifest-path src-tauri/Cargo.toml --format-version 1 --no-deps | ConvertFrom-Json
  if($LASTEXITCODE){throw 'Cargo metadata failed'}
  $releaseDirectory=Join-Path $metadata.target_directory 'release'
  $publishTarget=Join-Path $projectRoot 'dist/app'
  if(Test-Path -LiteralPath $publishTarget){
    $previous=Join-Path $projectRoot ('artifacts/previous-app-'+[DateTime]::Now.ToString('yyyyMMddHHmmssfff'))
    foreach($checked in @($publishTarget,$previous)){
      if(![IO.Path]::GetFullPath($checked).StartsWith($projectRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Build path escaped project root'}
    }
    New-Item -ItemType Directory -Path (Split-Path $previous) -Force|Out-Null
    Move-Item -LiteralPath $publishTarget -Destination $previous
  }
  New-Item -ItemType Directory -Force -Path $publishTarget|Out-Null
  Copy-Item -LiteralPath (Join-Path $releaseDirectory 'Weirdo.exe') -Destination $publishTarget
  Copy-Item -LiteralPath 'dist/native-broker/Weirdo.Broker.exe' -Destination $publishTarget
  foreach($notice in @('LICENSE','NOTICE.md','README.md')){Copy-Item -LiteralPath $notice -Destination $publishTarget}
  Copy-Item -LiteralPath 'licenses' -Destination $publishTarget -Recurse
  $setup=Get-ChildItem -LiteralPath (Join-Path $releaseDirectory 'bundle/nsis') -Filter '*.exe'|Sort-Object LastWriteTime -Descending|Select-Object -First 1
  if(!$setup){throw 'Tauri installer was not produced'}
  Copy-Item -LiteralPath $setup.FullName -Destination "dist/Weirdo-$appVersion-win-x64-setup.exe"
  $installer=Join-Path $projectRoot "dist/Weirdo-$appVersion-win-x64-setup.exe"
  @{version=$appVersion;url=$installer;sha256=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash;notes="Weirdo $appVersion"}|ConvertTo-Json|Set-Content -LiteralPath dist/update-local.json -Encoding utf8
  Get-ChildItem -LiteralPath dist -File | Where-Object Extension -in '.exe','.msi' | ForEach-Object {
    $hash=Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
    "$($hash.Hash)  $($_.Name)"
  } | Set-Content -LiteralPath dist/SHA256SUMS.txt -Encoding utf8
  Get-ChildItem -LiteralPath dist -File|Select-Object Name,Length
} finally {Pop-Location}
