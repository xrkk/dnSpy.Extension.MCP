# L4-b 增量5 dump 实车验证(自包含;在 VM 上以任意 PowerShell 运行)
# 前提:Linux 侧 http.server(192.168.204.1:8000)在线,dist 含 e96793e3 构建。
$ErrorActionPreference = 'Continue'
$ext = 'C:\Tools\dnSpy\bin\Extensions\dnSpy.Extension.MCP\dnSpy.Extension.MCP.x.dll'
$work = 'C:\Tools\MefCheck'

'== [1/6] 部署 e96793e3 并重启 dnSpy =='
Stop-Process -Name dnSpy -Force -ErrorAction SilentlyContinue
Start-Sleep 2
Invoke-WebRequest -Uri 'http://192.168.204.1:8000/dnSpy.Extension.MCP-net48.x.dll' -OutFile $ext -TimeoutSec 25
$hash = (Get-FileHash $ext -Algorithm SHA256).Hash.Substring(0,8)
"deployed sha8: $hash (expect E96793E3)"
Start-Process -FilePath 'C:\Tools\dnSpy\dnSpy.exe' -WorkingDirectory 'C:\Tools\dnSpy'
Start-Sleep 14
"health: $((Invoke-WebRequest -Uri 'http://localhost:15378/health' -UseBasicParsing -TimeoutSec 5).StatusCode)"

function Call($id, $name, $argsJson) {
    $b = '{"jsonrpc":"2.0","id":' + $id + ',"method":"tools/call","params":{"name":"' + $name + '","arguments":' + $argsJson + '}}'
    Set-Content "$work\c.json" $b -Encoding ascii
    (curl.exe -s --max-time 25 -X POST http://localhost:15378/ -H 'Accept: application/json' -H 'Content-Type: application/json' --data "@$work\c.json") | Out-String
}

'== [2/6] 启动调试会话并暂停 =='
$sha = (Get-FileHash 'C:\Tools\MefCheck\Vars.exe' -Algorithm SHA256).Hash.ToLower()
$L = Call 300 'debug_launch' ('{"request_id":"rv","target_path":"C:\\Tools\\MefCheck\\Vars.exe","expected_sha256":"' + $sha + '","launch_mode":"net48-exe","architecture":"x64","break_kind":"none"}')
$i = ($L | ConvertFrom-Json).result.content[0].text | ConvertFrom-Json
$sid = $i.debug_context.session_id
"launch ok=$($i.ok) sid=$sid"
Start-Sleep 2
$P = Call 301 'debug_pause' ('{"session_id":"' + $sid + '","generation":1,"request_id":"pv"}')
$pi = ($P | ConvertFrom-Json).result.content[0].text | ConvertFrom-Json
$ep = $pi.debug_context.pause_epoch
"pause epoch=$ep"
$M = Call 302 'debug_list_modules' ('{"session_id":"' + $sid + '","generation":1}')
$mi = ($M | ConvertFrom-Json).result.content[0].text | ConvertFrom-Json
$mod = ($mi.result.items | Where-Object { $_.name -like 'Vars*' })[0].module_handle
"target module: $mod"

'== [3/6] debug_dump_module(首次)=='
$D = Call 303 'debug_dump_module' ('{"session_id":"' + $sid + '","generation":1,"pause_epoch":' + $ep + ',"request_id":"dm1","module_handle":"' + $mod + '"}')
$di = ($D | ConvertFrom-Json).result.content[0].text | ConvertFrom-Json
if ($di.ok) {
    $a = $di.result.artifact
    "  ok size=$($a.size) sha=$($a.sha256.Substring(0,12)) kind=$($a.kind)"
    "  artifact file exists: $(Test-Path $a.path)"
    "  manifest exists: $(Test-Path $a.manifest_path)"
    "  on-disk sha match: $((Get-FileHash $a.path -Algorithm SHA256).Hash.ToLower() -eq $a.sha256)"
    "  source exe sha match: $($a.sha256 -eq $sha)"
    "== [4/6] 重复 dump(应 ALREADY_EXISTS)=="
    $D2 = Call 304 'debug_dump_module' ('{"session_id":"' + $sid + '","generation":1,"pause_epoch":' + $ep + ',"request_id":"dm2","module_handle":"' + $mod + '"}')
    $d2 = ($D2 | ConvertFrom-Json).result.content[0].text | ConvertFrom-Json
    "  dup ok=$($d2.ok) code=$($d2.error.code)"
} else {
    "  DUMP FAIL: $($di.error.code) $($di.error.message)"
    "== [4/6] 跳过(首次失败)=="
}

'== [5/6] read_memory(基址 MZ 头)=='
$R = Call 305 'debug_read_memory' ('{"session_id":"' + $sid + '","generation":1,"pause_epoch":' + $ep + ',"module_handle":"' + $mod + '","address":"0x890000","length":16,"encoding":"hex"}')
$ri = ($R | ConvertFrom-Json).result.content[0].text | ConvertFrom-Json
if ($ri.ok) { "  data=$($ri.result.data) MZ=$($ri.result.data.StartsWith('4d5a'))" } else { "  FAIL: $($ri.error.message)" }

'== [6/6] 终止会话 =='
$T = Call 306 'debug_terminate' ('{"session_id":"' + $sid + '","generation":1,"request_id":"tv"}')
$ti = ($T | ConvertFrom-Json).result.content[0].text | ConvertFrom-Json
"terminate ok=$($ti.ok) state=$($ti.debug_context.state)"
''
'ArtifactRoot 内容:'
Get-ChildItem 'C:\dnspy-mcp-artifacts' -Recurse -File | Select-Object FullName, Length | Format-Table -AutoSize | Out-String -Width 200
'DONE'
