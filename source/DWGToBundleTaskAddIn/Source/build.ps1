# Офлайн-сборка DWGToBundleTaskAddIn.dll через csc.exe (.NET Framework 4.x).
# Не требует .NET SDK / NuGet. Запуск: powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fw   = Split-Path $csc
$out  = Join-Path $root 'bin\DWGToBundleTaskAddIn.dll'

# EPDM-интероп: сначала стандартный путь PDM, иначе поиск.
$epdm = 'C:\Program Files\SOLIDWORKS PDM\EPDM.Interop.epdm.dll'
if (-not (Test-Path $epdm)) {
    $hit = Get-ChildItem 'C:\Program Files','C:\Program Files (x86)' -Recurse -Filter 'EPDM.Interop.epdm.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hit) { $epdm = $hit.FullName } else { throw 'EPDM.Interop.epdm.dll not found' }
}

$refs = @(
    "/r:`"$([IO.Path]::Combine($fw,'System.dll'))`"",
    "/r:`"$([IO.Path]::Combine($fw,'System.Core.dll'))`"",
    "/r:`"$([IO.Path]::Combine($fw,'System.Drawing.dll'))`"",
    "/r:`"$([IO.Path]::Combine($fw,'System.Windows.Forms.dll'))`"",
    "/r:`"$([IO.Path]::Combine($fw,'System.Xml.dll'))`"",
    "/r:`"$epdm`""
)

$srcs = @(Get-ChildItem -Path (Join-Path $root '*.cs'),(Join-Path $root 'Properties\*.cs') | ForEach-Object { "`"$($_.FullName)`"" })

# Вложенные ресурсы движка: батник + NETLOAD-плагин шьются внутрь DLL.
$resBat = Join-Path $root 'Resources\dwg2pdf.bat'
$resDll = Join-Path $root 'Resources\SheetLayoutDistributor.dll'
if (-not (Test-Path $resBat)) { throw "not found: $resBat" }
if (-not (Test-Path $resDll)) { throw "not found: $resDll" }
$res = @(
    "/resource:`"$resBat`",DWGToBundleTaskAddIn.Resources.dwg2pdf.bat",
    "/resource:`"$resDll`",DWGToBundleTaskAddIn.Resources.SheetLayoutDistributor.dll"
)

New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null

$cmd = @("/nologo","/target:library","/platform:anycpu","/optimize+","/codepage:65001","/out:`"$out`"") + $refs + $res + $srcs
Write-Host "csc ... -> $out"
Write-Host "EPDM: $epdm"
& $csc @cmd
if ($LASTEXITCODE -ne 0) { throw "csc EXIT=$LASTEXITCODE" }
Write-Host "OK: $((Get-Item $out).Length) bytes"
