param([Parameter(Mandatory)][string]$Type,[string]$Payload='{}')
$ErrorActionPreference='Stop'
$commandJson=$Type|ConvertTo-Json -Compress
$payloadJson=$Payload|ConvertFrom-Json|ConvertTo-Json -Depth 80 -Compress
$source=@"
(async () => {
  const id=crypto.randomUUID();
  return await new Promise((resolve,reject) => {
    const timer=setTimeout(()=>reject(new Error('Desktop request timed out')),45000);
    const handler=e=>{if(e.data.id===id){clearTimeout(timer);window.chrome.webview.removeEventListener('message',handler);resolve(e.data);}};
    window.chrome.webview.addEventListener('message',handler);
    window.chrome.webview.postMessage({id,type:$commandJson,payload:$payloadJson});
  });
})()
"@
$encoded=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($source))
agent-browser eval -b $encoded
