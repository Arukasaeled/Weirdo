param([Parameter(Mandatory)][int]$HostProcessId,[Parameter(Mandatory)][int]$BrokerProcessId,[Parameter(Mandatory)][string]$OutputPath,[string]$DataDir=(Join-Path $env:LOCALAPPDATA 'Kooky'),[int]$Samples=20)
$ErrorActionPreference='Stop'
$appDirectory=Split-Path (Get-Process -Id $HostProcessId).Path
[void][Reflection.Assembly]::LoadFrom((Join-Path $appDirectory 'Kooky.Core.dll'))
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $appDirectory 'Kooky.dll'))
$sampler=$assembly.GetType('Kooky.App.PerformanceSampler').GetMethod('SampleAsync',[Reflection.BindingFlags]'Public,Static')
$options=[Kooky.Core.JsonDefaults]::Options
[IO.File]::WriteAllText($OutputPath,'',[Text.UTF8Encoding]::new($false))
for($sampleIndex=0;$sampleIndex -lt $Samples;$sampleIndex++){
  $sessionObjects=@(& (Join-Path $PSScriptRoot 'rpc.ps1') -Type session.list -DataDir $DataDir)
  $sessionJson=ConvertTo-Json -InputObject $sessionObjects -Depth 15 -Compress
  $sessions=[Text.Json.JsonSerializer]::Deserialize($sessionJson,[Kooky.Core.SessionInfo[]],$options)
  $arguments=[object[]]::new(3);$arguments[0]=$BrokerProcessId;$arguments[1]=$HostProcessId;$arguments[2]=$sessions
  $report=$sampler.Invoke($null,$arguments).GetAwaiter().GetResult()
  $json=[Text.Json.JsonSerializer]::Serialize($report,$report.GetType(),$options)
  [IO.File]::AppendAllText($OutputPath,$json+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
}
[pscustomobject]@{Samples=$Samples;Output=$OutputPath}
