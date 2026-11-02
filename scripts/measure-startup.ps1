param(
    [string]$Executable = (Join-Path $env:LOCALAPPDATA 'Programs\Kooky\Kooky.exe'),
    [string]$ExpectedVersion = '0.1.4',
    [switch]$FunctionsOnly
)

$ErrorActionPreference = 'Stop'

if (-not ('KookyStartupNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class KookyStartupNative
{
    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message,
        UIntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out UIntPtr result);
}
'@
}

function Get-KookyWindowsEnvironment {
    $token = [IntPtr]::Zero
    $block = [IntPtr]::Zero
    try {
        if (-not [KookyStartupNative]::OpenProcessToken([KookyStartupNative]::GetCurrentProcess(), 0x000A, [ref]$token)) {
            throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
        }
        if (-not [KookyStartupNative]::CreateEnvironmentBlock([ref]$block, $token, $false)) {
            throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
        }
        $variables = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
        $cursor = $block
        while ([Runtime.InteropServices.Marshal]::ReadInt16($cursor) -ne 0) {
            $entry = [Runtime.InteropServices.Marshal]::PtrToStringUni($cursor)
            $separator = $entry.IndexOf('=', 1)
            if ($separator -gt 0) {
                $variables[$entry.Substring(0, $separator)] = $entry.Substring($separator + 1)
            }
            $cursor = [IntPtr]::Add($cursor, ($entry.Length + 1) * 2)
        }
        return ,$variables
    }
    finally {
        if ($block -ne [IntPtr]::Zero) { [void][KookyStartupNative]::DestroyEnvironmentBlock($block) }
        if ($token -ne [IntPtr]::Zero) { [void][KookyStartupNative]::CloseHandle($token) }
    }
}

function Start-KookyNormalEnvironment {
    param(
        [Parameter(Mandatory)][string]$ExecutablePath,
        [string[]]$Arguments = @(),
        [Collections.Generic.IDictionary[string,string]]$EnvironmentBlock
    )
    if ($null -eq $EnvironmentBlock) { $EnvironmentBlock = Get-KookyWindowsEnvironment }
    $start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($ExecutablePath))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($start.FileName)
    $start.Environment.Clear()
    foreach ($variable in $EnvironmentBlock.GetEnumerator()) {
        $start.Environment[$variable.Key] = $variable.Value
    }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    return [Diagnostics.Process]::Start($start)
}

function Find-KookyEnvironmentExecutable {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][Collections.Generic.IDictionary[string,string]]$EnvironmentBlock
    )
    if (-not $EnvironmentBlock.ContainsKey('PATH')) { return $null }
    foreach ($directory in $EnvironmentBlock['PATH'].Split(';', [StringSplitOptions]::RemoveEmptyEntries)) {
        $candidate = [IO.Path]::Combine($directory.Trim().Trim('"'), $Name)
        if ([IO.File]::Exists($candidate)) { return [IO.Path]::GetFullPath($candidate) }
    }
    return $null
}

if ($FunctionsOnly) { return }

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$acceptanceRoot = [IO.Path]::Combine($projectRoot, 'artifacts', 'acceptance')
[void][IO.Directory]::CreateDirectory($acceptanceRoot)
$executablePath = [IO.Path]::GetFullPath($Executable)
$installedFile = Get-Item -LiteralPath $executablePath
if ($installedFile.VersionInfo.FileVersion -ne ($ExpectedVersion + '.0')) {
    throw "Installed version $($installedFile.VersionInfo.FileVersion) does not match expected $ExpectedVersion.0."
}
$preexisting = @(Get-CimInstance Win32_Process -Filter "Name = 'Kooky.exe'")
if ($preexisting.Count -gt 0) {
    throw "Found $($preexisting.Count) existing Kooky process(es). Startup measurement requires a stopped app."
}
$normalEnvironment = Get-KookyWindowsEnvironment
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$environmentSummary = [ordered]@{
    source = 'userenv.dll CreateEnvironmentBlock, current Windows user token, inherit=false'
    resolvedPwsh = Find-KookyEnvironmentExecutable -Name 'pwsh.exe' -EnvironmentBlock $normalEnvironment
    resolvedWindowsPowerShell = Find-KookyEnvironmentExecutable -Name 'powershell.exe' -EnvironmentBlock $normalEnvironment
    hasTerm = $normalEnvironment.ContainsKey('TERM')
    hasNoColor = $normalEnvironment.ContainsKey('NO_COLOR')
    hasPsModulePath = $normalEnvironment.ContainsKey('PSModulePath')
}
if ($environmentSummary.resolvedPwsh) {
    $pwshFile = Get-Item -LiteralPath $environmentSummary.resolvedPwsh -Force
    $environmentSummary['pwshFileLength'] = $pwshFile.Length
    $environmentSummary['pwshIsAppExecutionAlias'] = $pwshFile.Length -eq 0 -and ($pwshFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
}
$environmentSummary['programFilesPowerShell7Exists'] = [IO.File]::Exists([IO.Path]::Combine($normalEnvironment['ProgramFiles'], 'PowerShell', '7', 'pwsh.exe'))
$report = [ordered]@{
    at = [DateTimeOffset]::Now.ToString('O')
    executable = $executablePath
    fileVersion = $installedFile.VersionInfo.FileVersion
    productVersion = $installedFile.VersionInfo.ProductVersion
    assemblySha256 = (Get-FileHash -LiteralPath ([IO.Path]::Combine($installedFile.DirectoryName, 'Kooky.dll')) -Algorithm SHA256).Hash
    expectedVersion = $ExpectedVersion
    debug = $false
    environment = $environmentSummary
    samples = @()
    completed = $false
}
$reportPath = [IO.Path]::Combine($acceptanceRoot, "startup-$runId.json")

try {
    for ($sampleNumber = 1; $sampleNumber -le 3; $sampleNumber++) {
        $profile = [IO.Path]::Combine($acceptanceRoot, "startup-profile-$runId-$sampleNumber")
        if ([IO.Directory]::Exists($profile)) { throw 'The clean-profile path already exists.' }
        [void][IO.Directory]::CreateDirectory($profile)
        $startupPath = [IO.Path]::Combine($profile, 'startup.json')
        $sample = [ordered]@{
            sample = $sampleNumber
            profile = $profile
            processId = $null
            brokerProcessId = $null
            recordedElapsedMs = $null
            observedReadyMs = $null
            visible = $false
            responsive = $false
            nativeWindowHandle = $null
            version = $null
            debug = $null
            uiExited = $false
            brokerExited = $false
            remainingOwnedProcesses = @()
            errors = @()
            passed = $false
        }
        $report.samples += $sample
        $process = $null
        $brokerProcess = $null
        $ownedProcessIds = @()
        $measurementFailure = $null
        try {
            $timer = [Diagnostics.Stopwatch]::StartNew()
            $process = Start-KookyNormalEnvironment -ExecutablePath $executablePath -Arguments @('--data-dir', $profile) -EnvironmentBlock $normalEnvironment
            $sample.processId = $process.Id
            $ownedProcessIds += $process.Id
            $startup = $null
            while ($timer.Elapsed.TotalSeconds -lt 35) {
                if ($process.HasExited) { throw "UI exited before startup was recorded, code $($process.ExitCode)." }
                if ([IO.File]::Exists($startupPath)) {
                    try { $candidate = [IO.File]::ReadAllText($startupPath) | ConvertFrom-Json }
                    catch { $candidate = $null }
                    if ($null -ne $candidate -and $candidate.processId -eq $process.Id) {
                        $startup = $candidate
                        break
                    }
                }
                Start-Sleep -Milliseconds 50
            }
            if ($null -eq $startup) { throw 'No matching startup report within 35 seconds.' }
            $sample.recordedElapsedMs = [Math]::Round([double]$startup.elapsedMs, 2)
            $sample.observedReadyMs = [Math]::Round($timer.Elapsed.TotalMilliseconds, 2)
            $sample.version = $startup.version
            $sample.debug = $startup.debug
            if ($startup.version -ne $ExpectedVersion -or $startup.debug) { throw 'Startup version/debug state did not match the installed normal launch.' }
            if (-not $startup.executable.Equals($executablePath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Startup report references another executable.' }
            $process.Refresh()
            $window = $process.MainWindowHandle
            $sample.nativeWindowHandle = '0x' + $window.ToString('X')
            $sample.visible = $window -ne [IntPtr]::Zero -and [KookyStartupNative]::IsWindowVisible($window)
            $messageResult = [UIntPtr]::Zero
            $sample.responsive = $window -ne [IntPtr]::Zero -and [KookyStartupNative]::SendMessageTimeoutW($window, 0, [UIntPtr]::Zero, [IntPtr]::Zero, 3, 1500, [ref]$messageResult) -ne [IntPtr]::Zero
            if (-not $sample.visible -or -not $sample.responsive) { throw 'The main native window is not both visible and responsive.' }
            $bootstrap = & (Join-Path $PSScriptRoot 'rpc.ps1') -Type 'bootstrap' -DataDir $profile
            $sample.brokerProcessId = $bootstrap.brokerPid
            $ownedProcessIds += [int]$bootstrap.brokerPid
            $brokerProcess = [Diagnostics.Process]::GetProcessById([int]$bootstrap.brokerPid)
        }
        catch {
            $measurementFailure = $_
            $sample.errors += $_.Exception.Message
        }
        finally {
            if ($null -ne $process -and -not $process.HasExited) {
                try { $null = & (Join-Path $PSScriptRoot 'rpc.ps1') -Type 'broker.shutdown' -DataDir $profile }
                catch { $sample.errors += "Graceful shutdown failed. $($_.Exception.Message)" }
            }
            if ($null -ne $process) { $sample.uiExited = $process.WaitForExit(10000) }
            if ($null -ne $brokerProcess) { $sample.brokerExited = $brokerProcess.WaitForExit(10000) }
            $remaining = @(Get-CimInstance Win32_Process -Filter "Name = 'Kooky.exe'" | Where-Object {
                $ownedProcessIds -contains [int]$_.ProcessId -or ($_.CommandLine -and $_.CommandLine.Contains($profile))
            })
            $sample.remainingOwnedProcesses = @($remaining | Select-Object ProcessId, ParentProcessId, CommandLine)
            $errorPath = [IO.Path]::Combine($profile, 'errors.log')
            if ([IO.File]::Exists($errorPath) -and (Get-Item -LiteralPath $errorPath).Length -gt 0) {
                $sample.errors += [IO.File]::ReadAllText($errorPath)
            }
            $sample.passed = $null -eq $measurementFailure -and $sample.uiExited -and $sample.brokerExited -and $remaining.Count -eq 0 -and $sample.errors.Count -eq 0
            $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8
            if ($null -ne $brokerProcess) { $brokerProcess.Dispose() }
            if ($null -ne $process) { $process.Dispose() }
        }
        if (-not $sample.passed) { throw "Startup sample $sampleNumber failed. Inspect $reportPath." }
        Write-Output "PASS sample $sampleNumber, ready $($sample.recordedElapsedMs) ms, visible and responsive, UI and broker exited."
    }
    $elapsedValues = @($report.samples | ForEach-Object { $_.recordedElapsedMs } | Sort-Object)
    $report['timing'] = [ordered]@{
        minimumMs = $elapsedValues[0]
        medianMs = $elapsedValues[1]
        maximumMs = $elapsedValues[2]
        meanMs = [Math]::Round(($elapsedValues | Measure-Object -Average).Average, 2)
    }
    $report.completed = $true
}
finally {
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8
}

Write-Output $reportPath
Write-Output ($environmentSummary | ConvertTo-Json -Compress)
