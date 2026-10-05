# OrbDock 构建 / 发布脚本
# 用法：
#   .\build.ps1              仅构建（Debug 输出到 src\OrbDock\bin）
#   .\build.ps1 -Publish     构建并发布到 .\app（可直接运行）
#   .\build.ps1 -Publish -Clean   先清理再发布

param(
    [switch]$Publish,
    [switch]$Clean,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$proj = Join-Path $root 'src\OrbDock\OrbDock.csproj'
$out  = Join-Path $root 'app'

if (-not (Test-Path $proj)) { throw "找不到工程文件：$proj" }

if ($Clean) {
    Write-Host '清理 bin/obj ...' -ForegroundColor Cyan
    Get-ChildItem (Join-Path $root 'src\OrbDock') -Include bin,obj -Recurse -Directory |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "构建 $Configuration ..." -ForegroundColor Cyan
if ($Publish) {
    dotnet publish $proj -c $Configuration -o $out --nologo
    Write-Host "已发布到：$out" -ForegroundColor Green
    Write-Host "运行：$out\OrbDock.exe   应急关闭：Ctrl+Alt+F12" -ForegroundColor Green
} else {
    dotnet build $proj -c $Configuration --nologo
}

if ($LASTEXITCODE -ne 0) { throw "构建失败，退出码 $LASTEXITCODE" }
