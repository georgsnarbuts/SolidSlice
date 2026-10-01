# Builds bin\SolidSliceLink.dll with the .NET Framework compiler that ships with Windows
# (no Visual Studio needed). The SolidWorks install is found via the registry;
# pass -SolidWorksDir to override.
param([string]$SolidWorksDir)
$ErrorActionPreference = 'Stop'

if (-not $SolidWorksDir) {
    # newest "SOLIDWORKS 20xx" key that has a Setup\SolidWorks Folder value
    $SolidWorksDir = Get-ChildItem HKLM:\SOFTWARE\SolidWorks -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^SOLIDWORKS \d{4}$' } |
        Sort-Object PSChildName -Descending |
        ForEach-Object { (Get-ItemProperty "$($_.PSPath)\Setup" -ErrorAction SilentlyContinue).'SolidWorks Folder' } |
        Where-Object { $_ -and (Test-Path $_) } |
        Select-Object -First 1
}
if (-not $SolidWorksDir) { $SolidWorksDir = "$env:ProgramFiles\SOLIDWORKS Corp\SOLIDWORKS" }

$redist = Join-Path $SolidWorksDir 'api\redist'
if (-not (Test-Path "$redist\SolidWorks.Interop.sldworks.dll")) {
    throw "SolidWorks API DLLs not found in '$redist'. Re-run with -SolidWorksDir <SOLIDWORKS install folder>."
}

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

$refs = 'SolidWorks.Interop.sldworks.dll', 'SolidWorks.Interop.swconst.dll', 'SolidWorks.Interop.swpublished.dll'
foreach ($dll in $refs) { Copy-Item (Join-Path $redist $dll) $out -Force }

& $csc /nologo /target:library /platform:x64 /optimize+ "/out:$out\SolidSliceLink.dll" `
    ($refs | ForEach-Object { "/reference:$out\$_" }) `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    (Join-Path $PSScriptRoot 'src\*.cs')
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $out\SolidSliceLink.dll (SolidWorks API from $redist)"
