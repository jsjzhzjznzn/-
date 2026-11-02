# 重启 .NET 服务端并把 Agent 地址指向 Node 版（联调用脚本，P7 E2E）
# 说明：从 run_server.bat 里解析 CS_DB_* 凭据（不回显、不落盘），
#       仅额外覆盖 CS_AGENT_PYTHON_URL，日志写 server_boot.log。
param(
    [string]$AgentUrl = "http://127.0.0.1:8890",
    [string]$LogFile = "server_boot_node.log"
)

$repo = "D:\tuanjieproject\frined\competeserves"
$bat = Join-Path $repo "run_server.bat"

foreach ($line in Get-Content $bat) {
    if ($line -match 'set\s+"?(CS_[A-Z_]+)=([^"]*)"?') {
        Set-Item -Path "env:$($matches[1])" -Value $matches[2].Trim()
    }
}
$env:CS_AGENT_PYTHON_URL = $AgentUrl

Write-Host "[restart-server] Agent 指向 $AgentUrl，凭据已从 run_server.bat 载入（不回显）"
Set-Location (Join-Path $repo "Server")
dotnet run 2>&1 | Out-File (Join-Path $repo $LogFile) -Encoding utf8
