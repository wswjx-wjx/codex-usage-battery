param([string]$OutputName = 'CodexUsageBattery.exe')

$ErrorActionPreference = 'Stop'
$batteryCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $batteryCompiler -PathType Leaf)) {
    throw 'The 64-bit .NET Framework C# compiler was not found.'
}
$batteryOutput = if ([IO.Path]::IsPathRooted($OutputName)) { $OutputName } else { Join-Path $PSScriptRoot $OutputName }
$batteryOutput = [IO.Path]::GetFullPath($batteryOutput)
$batteryOutputDirectory = [IO.Path]::GetDirectoryName($batteryOutput)
if (-not (Test-Path -LiteralPath $batteryOutputDirectory -PathType Container)) {
    throw 'The output directory does not exist.'
}
$batterySource = Join-Path $PSScriptRoot 'CodexUsageBattery.cs'
$batteryThemes = Join-Path $PSScriptRoot 'BatteryThemes.cs'
& $batteryCompiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /codepage:65001 "/out:$batteryOutput" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll $batterySource $batteryThemes
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output $batteryOutput
