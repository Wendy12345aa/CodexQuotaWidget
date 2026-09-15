$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $projectRoot 'src\Program.cs'
$manifest = Join-Path $projectRoot 'src\app.manifest'
$outputDir = Join-Path $projectRoot 'dist'
$output = Join-Path $outputDir 'CodexQuotaWidget.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $compiler)) {
    throw '.NET Framework 4.x compiler was not found. Enable the Windows .NET Framework feature, then run this script again.'
}

New-Item -ItemType Directory -Force $outputDir | Out-Null

& $compiler /nologo /target:winexe /optimize+ /platform:anycpu /utf8output `
    /win32manifest:$manifest `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    /out:$output $source

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

Write-Host "Built: $output"
