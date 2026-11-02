param([Parameter(Mandatory)][int]$HostProcessId,[Parameter(Mandatory)][int]$BrokerProcessId,[Parameter(Mandatory)][string]$OutputPath,[string]$DataDir=(Join-Path $env:LOCALAPPDATA 'Weirdo'),[int]$Samples=20)
$ErrorActionPreference='Stop'
$bootstrap = & (Join-Path $PSScriptRoot 'rpc.ps1') -Type bootstrap -DataDir $DataDir
if ($bootstrap.brokerPid -ne $BrokerProcessId) { throw 'The selected profile belongs to another broker.' }
$hostProcess = Get-Process -Id $HostProcessId
if ($hostProcess.ProcessName -ne 'Weirdo') { throw 'The selected host is not Weirdo.' }
[IO.File]::WriteAllText($OutputPath,'',[Text.UTF8Encoding]::new($false))
for($sampleIndex=0;$sampleIndex -lt $Samples;$sampleIndex++){
  $report = & (Join-Path $PSScriptRoot 'rpc.ps1') -Type performance -DataDir $DataDir -Payload (@{hostPid=$HostProcessId}|ConvertTo-Json -Compress)
  [IO.File]::AppendAllText($OutputPath,($report|ConvertTo-Json -Depth 15 -Compress)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
}
[pscustomobject]@{Samples=$Samples;Output=$OutputPath}
