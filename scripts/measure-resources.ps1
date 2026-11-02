param(
    [Parameter(Mandatory)][int]$HostProcessId,
    [Parameter(Mandatory)][int]$BrokerProcessId,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA 'Weirdo'),
    [ValidateRange(1, 300)][int]$Samples = 20
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assemblyDirectory = Join-Path $projectRoot 'src\Weirdo.Broker\bin\Release\net10.0-windows\win-x64'
$outputFile = [IO.Path]::GetFullPath($OutputPath)
if ([IO.File]::Exists($outputFile)) {
    throw 'The output file already exists. Choose a new path to retain earlier measurements.'
}

# Load the already-built managed implementation into this independent observer.
# No performance request runs inside the host or broker during the sample window.
$coreAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $assemblyDirectory 'Weirdo.Core.dll'))
$brokerAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $assemblyDirectory 'Weirdo.Broker.dll'))
$contextType = $brokerAssembly.GetType('Weirdo.BrokerHost.BrokerJsonContext', $true)
$staticFlags = [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Static
$instanceFlags = [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Instance
$context = $contextType.GetProperty('Default', $staticFlags).GetValue($null)
$sessionTypeInfo = $contextType.GetProperty('SessionInfoArray', $instanceFlags).GetValue($context)
$measurementsTypeInfo = $contextType.GetProperty('ResourceMeasurements', $instanceFlags).GetValue($context)
$samplerType = $brokerAssembly.GetType('Weirdo.BrokerHost.PerformanceSampler', $true)
$sampleMethod = $samplerType.GetMethod('SampleAsync', $staticFlags)
if ($null -eq $sampleMethod) { throw 'The compiled broker sampler is unavailable.' }

$rpcScript = Join-Path $PSScriptRoot 'rpc.ps1'
$bootstrap = & $rpcScript -Type bootstrap -DataDir $DataDir
if ($bootstrap.brokerPid -ne $BrokerProcessId) {
    throw 'The selected profile belongs to another broker.'
}
$compiledVersion = $brokerAssembly.GetName().Version.ToString(3)
if ($bootstrap.appVersion -ne $compiledVersion) {
    throw "The running broker version $($bootstrap.appVersion) differs from compiled sampler version $compiledVersion."
}
$hostProcess = [Diagnostics.Process]::GetProcessById($HostProcessId)
$brokerProcess = [Diagnostics.Process]::GetProcessById($BrokerProcessId)
try {
    if ($hostProcess.ProcessName -ne 'Weirdo') { throw 'The selected host is not Weirdo.' }
    if ($brokerProcess.ProcessName -ne 'Weirdo.Broker') { throw 'The selected service is not Weirdo.Broker.' }
    if ($PID -in @($HostProcessId, $BrokerProcessId)) { throw 'The observer must run outside the measured application.' }

    $sessionsJson = ConvertTo-Json -InputObject @($bootstrap.sessions) -Depth 30 -Compress
    $sessions = [Text.Json.JsonSerializer]::Deserialize($sessionsJson, [Text.Json.Serialization.Metadata.JsonTypeInfo]$sessionTypeInfo)
    $runningBefore = @($sessions | Where-Object Running | Sort-Object Id | ForEach-Object { "$($_.Id):$($_.ProcessId)" })
    $arguments = [object[]]::new(3)
    $arguments[0] = $BrokerProcessId
    $arguments[1] = $HostProcessId
    $arguments[2] = $sessions
    $reports = [Collections.Generic.List[object]]::new()

    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputFile))
    $stream = [IO.FileStream]::new($outputFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
    try {
        for ($sampleIndex = 0; $sampleIndex -lt $Samples; $sampleIndex++) {
            $hostProcess.Refresh()
            $brokerProcess.Refresh()
            if ($hostProcess.HasExited -or $brokerProcess.HasExited) {
                throw 'A selected application process exited during measurement.'
            }

            $sampleTask = $sampleMethod.Invoke($null, $arguments)
            $report = $sampleTask.GetAwaiter().GetResult()
            if ($report.Processes.Pid -contains $PID) {
                throw 'The external observer was included in the selected process tree. Use an unrelated PowerShell process.'
            }
            if ($report.Processes.Pid -notcontains $HostProcessId -or $report.Processes.Pid -notcontains $BrokerProcessId) {
                throw 'The sample did not include both selected application processes.'
            }
            $json = [Text.Json.JsonSerializer]::Serialize($report, [Text.Json.Serialization.Metadata.JsonTypeInfo]$measurementsTypeInfo)
            $writer.WriteLine($json)
            $writer.Flush()
            $reports.Add($report)
        }
    }
    finally { $writer.Dispose() }

    # A lightweight state check after the sample window detects process churn.
    $after = & $rpcScript -Type bootstrap -DataDir $DataDir
    $runningAfter = @($after.sessions | Where-Object running | Sort-Object id | ForEach-Object { "$($_.id):$($_.processId)" })
    if ($after.brokerPid -ne $BrokerProcessId -or ($runningBefore -join '|') -ne ($runningAfter -join '|')) {
        throw 'The active terminal set changed during measurement. The partial observations were retained.'
    }

    $groupNames = @($reports | ForEach-Object { $_.Groups.Name } | Sort-Object -Unique)
    $summary = foreach ($groupName in $groupNames) {
        $groupSamples = @($reports | ForEach-Object { $_.Groups | Where-Object Name -EQ $groupName })
        [pscustomobject]@{
            Name = $groupName
            MeanCpuPercent = [Math]::Round(($groupSamples.CpuPercent | Measure-Object -Average).Average, 6)
            MaximumCpuPercent = [Math]::Round(($groupSamples.CpuPercent | Measure-Object -Maximum).Maximum, 6)
            MeanWorkingSetMiB = [Math]::Round(($groupSamples.WorkingSetBytes | Measure-Object -Average).Average / 1MB, 3)
            MeanPrivateMiB = [Math]::Round(($groupSamples.PrivateBytes | Measure-Object -Average).Average / 1MB, 3)
        }
    }
    [pscustomobject]@{
        Samples = $reports.Count
        Output = $outputFile
        ObserverProcessId = $PID
        HostProcessId = $HostProcessId
        BrokerProcessId = $BrokerProcessId
        ActiveTerminals = $runningBefore.Count
        Version = $compiledVersion
        Method = 'External PowerShell observer using compiled PerformanceSampler and explicit generated JSON metadata'
        Groups = @($summary)
    }
}
finally {
    $hostProcess.Dispose()
    $brokerProcess.Dispose()
}
