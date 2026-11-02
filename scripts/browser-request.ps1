param([Parameter(Mandatory)][string]$Type,[string]$Payload='{}',[string]$BrowserSession='')
$ErrorActionPreference='Stop'
$commandJson=$Type|ConvertTo-Json -Compress
$payloadJson=$Payload|ConvertFrom-Json|ConvertTo-Json -Depth 80 -Compress
$source="window.__TAURI_INTERNALS__.invoke('host_request',{kind:$commandJson,payload:$payloadJson})"
$encoded=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($source))
if($BrowserSession){agent-browser --session $BrowserSession eval -b $encoded}
else{agent-browser eval -b $encoded}
