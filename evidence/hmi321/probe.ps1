#Requires -Version 7
# hmi#321 只读探针：经 ssh agv02/agv03 以 pwsh -EncodedCommand 运行。只列文件名、大小、日期与磁盘空间，不读日志内容。
$ErrorActionPreference = 'Continue'
"hostname=$env:COMPUTERNAME now=$((Get-Date).ToString('o'))"
foreach ($d in 'C:\8005\OnboardHmi\logs','C:\Users\HT\AppData\Local\SQCD\8005AGV\logs') {
  "=== DIR $d exists=$(Test-Path $d)"
  if (Test-Path $d) {
    Get-ChildItem -LiteralPath $d -Recurse -File | Sort-Object LastWriteTime | ForEach-Object {
      '{0}`t{1}`t{2}`t{3}' -f $_.FullName.Substring($d.Length), $_.Length, $_.CreationTime.ToString('s'), $_.LastWriteTime.ToString('s')
    }
  }
}
"=== DRIVES"
Get-PSDrive -PSProvider FileSystem | ForEach-Object { '{0}`tused={1}`tfree={2}' -f $_.Name,$_.Used,$_.Free }
foreach ($d in 'C:\8005\OnboardHmi.previous\logs') {
  "=== DIR $d exists=$(Test-Path $d)"
  if (Test-Path $d) { Get-ChildItem -LiteralPath $d -File | Sort-Object Name | ForEach-Object { '{0}`t{1}`t{2}`t{3}' -f $_.Name,$_.Length,$_.CreationTime.ToString('s'),$_.LastWriteTime.ToString('s') } }
}
"=== VOLUME"
Get-Volume -DriveLetter C | ForEach-Object { 'C`tsize={0}`tfree={1}`tfs={2}' -f $_.Size,$_.SizeRemaining,$_.FileSystem }
