param([Parameter(Mandatory)][int]$KookyProcessId,[string]$WindowName,[string]$InvokeName,[string]$SetValueId,[string]$Text)
$ErrorActionPreference='Stop'
$desktopRuntime=Get-ChildItem -LiteralPath "$env:ProgramFiles\dotnet\shared\Microsoft.WindowsDesktop.App" -Directory | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
foreach($assembly in @('UIAutomationTypes.dll','UIAutomationClient.dll')){[void][Reflection.Assembly]::LoadFrom((Join-Path $desktopRuntime.FullName $assembly))}
$condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$KookyProcessId)
$windows=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$condition)
$found=$false
foreach($window in $windows){
  if($WindowName -and $window.Current.Name -ne $WindowName){continue}
  $elements=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
  foreach($element in $elements){
    $type=$element.Current.ControlType.ProgrammaticName
    if($InvokeName -and $element.Current.Name -eq $InvokeName -and $type -in @('ControlType.Button','ControlType.MenuItem')){
      $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();$found=$true;return
    } elseif($SetValueId -and $element.Current.AutomationId -eq $SetValueId){
      $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text);$element.SetFocus();$found=$true;return
    } elseif(!$InvokeName -and !$SetValueId -and $type -in @('ControlType.Edit','ControlType.Button')){
      [pscustomobject]@{Window=$window.Current.Name;Name=$element.Current.Name;Id=$element.Current.AutomationId;Type=$type;Enabled=$element.Current.IsEnabled}
    }
  }
}
if(($InvokeName -or $SetValueId)-and !$found){throw 'Requested native control was not found in the specified process/window.'}
