$ErrorActionPreference='Stop'
$t='D:\zhengyushao\control-server-v2-ops\sqlite-tools'
foreach($d in 'SQLitePCLRaw.core.dll','SQLitePCLRaw.provider.e_sqlite3.dll','SQLitePCLRaw.batteries_v2.dll','Microsoft.Data.Sqlite.dll'){ Add-Type -Path (Join-Path $t $d) }
$env:PATH = "$t;$env:PATH"
[SQLitePCL.Batteries_V2]::Init()
$c=[Microsoft.Data.Sqlite.SqliteConnection]::new('Data Source=C:\ProgramData\8005\ControlServer.V2\data\controlserver.db;Mode=ReadOnly;Pooling=False')
$c.Open()
function Q($label,$sql){ "== $label"; $cmd=$c.CreateCommand(); $cmd.CommandText=$sql; $r=$cmd.ExecuteReader(); while($r.Read()){ ($(0..($r.FieldCount-1) | % { $r.GetValue($_) }) -join ' | ') }; $r.Close() }
try {
Q 'sublot' "SELECT DISTINCT json_extract(i.value,'$.sublot') s, length(json_extract(i.value,'$.sublot')) n FROM ProtocolOutbox o, json_each(o.PayloadJson,'$.payload.items') i WHERE o.MessageType='CurrentStopWorklistSnapshot' ORDER BY n DESC LIMIT 8"
Q 'eqp' "SELECT DISTINCT json_extract(i.value,'$.eqp') s, length(json_extract(i.value,'$.eqp')) n FROM ProtocolOutbox o, json_each(o.PayloadJson,'$.payload.items') i WHERE o.MessageType='CurrentStopWorklistSnapshot' ORDER BY n DESC LIMIT 12"
Q 'station(worklist)' "SELECT DISTINCT json_extract(o.PayloadJson,'$.payload.stationId') s, length(json_extract(o.PayloadJson,'$.payload.stationId')) n FROM ProtocolOutbox o WHERE o.MessageType='CurrentStopWorklistSnapshot' ORDER BY n DESC LIMIT 8"
Q 'station(plan)' "SELECT DISTINCT json_extract(l.value,'$.stationId') s, length(json_extract(l.value,'$.stationId')) n, json_extract(l.value,'$.legType') FROM ProtocolOutbox o, json_each(o.PayloadJson,'$.payload.legs') l WHERE o.MessageType='UpcomingStopPlanSnapshot' ORDER BY n DESC LIMIT 8"
Q 'worktype' "SELECT DISTINCT json_extract(i.value,'$.workType') FROM ProtocolOutbox o, json_each(o.PayloadJson,'$.payload.items') i WHERE o.MessageType='CurrentStopWorklistSnapshot'"
Q 'agv' "SELECT DISTINCT json_extract(PayloadJson,'$.agvId') FROM ProtocolOutbox WHERE MessageType='UpcomingStopPlanSnapshot'"
Q 'sample item' "SELECT i.value FROM ProtocolOutbox o, json_each(o.PayloadJson,'$.payload.items') i WHERE o.MessageType='CurrentStopWorklistSnapshot' ORDER BY o.rowid DESC LIMIT 1"
} finally { $c.Close(); $c.Dispose() }
