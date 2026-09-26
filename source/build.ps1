# Builds LiveWall.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK or install needed.
param([string]$OutDir = (Join-Path $PSScriptRoot 'bin'))
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$src = Join-Path $PSScriptRoot 'src'
New-Item -ItemType Directory -Force $OutDir | Out-Null

$icon = Join-Path $src 'app.ico'
if (-not (Test-Path $icon)) {
    $tool = Join-Path $OutDir 'MakeIcon.exe'
    & $csc /nologo /target:exe /r:System.Drawing.dll "/out:$tool" (Join-Path $PSScriptRoot 'tools\MakeIcon.cs')
    if ($LASTEXITCODE -ne 0) { throw 'MakeIcon build failed' }
    & $tool $icon
    Remove-Item $tool
}

$files = @(Get-ChildItem $src -Recurse -Filter *.cs | ForEach-Object { $_.FullName })
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 /nowarn:0649 `
    "/out:$(Join-Path $OutDir 'LiveWall.exe')" `
    "/win32manifest:$(Join-Path $src 'app.manifest')" "/win32icon:$icon" "/resource:$icon,LiveWall.app.ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    $files
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Copy-Item (Join-Path $src 'LiveWall.exe.config') (Join-Path $OutDir 'LiveWall.exe.config') -Force
Get-Item (Join-Path $OutDir 'LiveWall.exe') | Select-Object FullName, Length
