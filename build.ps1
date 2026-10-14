$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sourcePath = Join-Path $PSScriptRoot 'AccountSwitcher.cs'
$manifestPath = Join-Path $PSScriptRoot 'app.manifest'
$iconPath = Join-Path $PSScriptRoot 'AccountSwitcher.ico'
$outputPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'CodexAccountSwitcher.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '需要 Windows .NET Framework 4.x 编译器。' }
& $compilerPath /nologo /target:winexe /platform:anycpu /optimize+ "/out:$outputPath" "/win32manifest:$manifestPath" "/win32icon:$iconPath" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $sourcePath
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Write-Output "已生成：$outputPath"
