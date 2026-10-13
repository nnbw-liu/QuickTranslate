<#
.SYNOPSIS
  一键构建 QuickTranslate：C++ 原生库 (NativeCore) + C# WPF 主程序，并发布到 dist\QuickTranslate。

.PARAMETER SelfContained
  发布为自包含版本（目标机无需安装 .NET，体积更大）。
  默认发布为框架依赖版本（需要 .NET 9 Desktop Runtime）。

.PARAMETER NoPublish
  只编译不发布（bin 输出即可）。
#>
param(
    [switch]$SelfContained,
    [switch]$NoPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$nativeDir = Join-Path $root 'src\NativeCore'
$appDir = Join-Path $root 'src\QuickTranslate'
$out = Join-Path $root 'dist\QuickTranslate'

# 定位 MSBuild
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path $msbuild)) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $vs = & $vswhere -latest -products * -property installationPath
        if ($vs) { $msbuild = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe' }
    }
}
if (-not (Test-Path $msbuild)) { throw '未找到 MSBuild，请安装 Visual Studio（含 C++ 桌面开发工作负载）。' }

Write-Host '==> [1/4] 生成应用图标' -ForegroundColor Cyan
& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\make-icon.ps1')

Write-Host '==> [2/4] 编译 C++ 原生库 NativeCore.dll' -ForegroundColor Cyan
& $msbuild (Join-Path $nativeDir 'NativeCore.vcxproj') -p:Configuration=Release -p:Platform=x64 -m -v:m -nologo
if ($LASTEXITCODE -ne 0) { throw 'NativeCore 编译失败' }

$nativeDll = Join-Path $nativeDir 'build\bin\Release\NativeCore.dll'
if (-not (Test-Path $nativeDll)) { throw "缺少 $nativeDll" }

if ($NoPublish) {
    Write-Host '==> [3/3] 仅编译 C# 主程序（不发布）' -ForegroundColor Cyan
    dotnet build (Join-Path $appDir 'QuickTranslate.csproj') -c Release -v m -nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build 失败' }
    $target = Join-Path $appDir 'bin\Release\net9.0-windows10.0.19041.0'
    Copy-Item $nativeDll $target -Force
    Write-Host ''
    Write-Host "构建完成 -> $target" -ForegroundColor Green
    exit 0
}

Write-Host '==> [3/4] 编译并发布 C# 主程序' -ForegroundColor Cyan
if ($SelfContained) {
    dotnet publish (Join-Path $appDir 'QuickTranslate.csproj') -c Release -r win-x64 --self-contained true -o $out
}
else {
    dotnet publish (Join-Path $appDir 'QuickTranslate.csproj') -c Release --self-contained false -o $out
}
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

Write-Host '==> [4/4] 复制原生库与图标' -ForegroundColor Cyan
Copy-Item $nativeDll $out -Force
Copy-Item (Join-Path $root 'assets\app.ico') $out -Force

Write-Host ''
Write-Host "构建完成 -> $out" -ForegroundColor Green
Write-Host '运行: 双击 QuickTranslate.exe；配置: 右键托盘图标 -> 首选项'
