param([Parameter(Mandatory)][string]$Type,[string]$Payload='{}',[string]$DataDir=(Join-Path $env:LOCALAPPDATA 'Weirdo'))
$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$key=$identity+[IO.Path]::GetFullPath($DataDir).ToUpperInvariant()
$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($key)))
$pipe=[IO.Pipes.NamedPipeClientStream]::new('.','weirdo-'+$hash.Substring(0,24),[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
try {
  $pipe.Connect(10000)
  $reader=[IO.StreamReader]::new($pipe,[Text.Encoding]::UTF8,$false,65536,$true)
  $writer=[IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),65536,$true)
  $writer.AutoFlush=$true
  $requestId=[Guid]::NewGuid().ToString('N')
  $writer.WriteLine((@{id=$requestId;type=$Type;payload=($Payload|ConvertFrom-Json)}|ConvertTo-Json -Depth 80 -Compress))
  while($null -ne ($line=$reader.ReadLine())) {
    $value=$line|ConvertFrom-Json
    if($value.id -eq $requestId){if(!$value.ok){throw $value.error};$value.result;break}
  }
} finally {$pipe.Dispose()}
