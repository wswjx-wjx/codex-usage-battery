param([string]$OutputDirectory = 'dist')

$ErrorActionPreference = 'Stop'
$batteryVersion = '1.3.2-preview'
$batteryOutputDir = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $PSScriptRoot $OutputDirectory }
$batteryOutputDir = [IO.Path]::GetFullPath($batteryOutputDir)
if (-not (Test-Path -LiteralPath $batteryOutputDir)) {
    New-Item -ItemType Directory -Path $batteryOutputDir | Out-Null
}
$batteryZipName = 'CodexUsageBattery-Windows-' + $batteryVersion + '.zip'
$batteryZipPath = Join-Path $batteryOutputDir $batteryZipName
$batteryHashPath = $batteryZipPath + '.sha256'
if ((Test-Path -LiteralPath $batteryZipPath) -or (Test-Path -LiteralPath $batteryHashPath)) {
    throw 'A package or checksum with this name already exists. Choose an empty output directory.'
}

$batteryStage = Join-Path $batteryOutputDir ('.CodexUsageBattery-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $batteryStage | Out-Null
try {
    $batteryExe = Join-Path $batteryStage 'CodexUsageBattery.exe'
    Write-Output 'Building the executable...'
    & (Join-Path $PSScriptRoot 'build.ps1') -OutputName $batteryExe | Out-Null
    Write-Output 'Running the core checks...'
    $batteryTest = Start-Process -FilePath $batteryExe -ArgumentList '--core-test' -WorkingDirectory $batteryStage -WindowStyle Hidden -PassThru
    if (-not $batteryTest.WaitForExit(30000)) {
        $batteryTest.Kill()
        $batteryTest.WaitForExit()
        throw 'Core verification timed out; package was not created.'
    }
    if ($batteryTest.ExitCode -ne 0) {
        $batteryError = Join-Path $batteryStage 'last-error.txt'
        if (Test-Path -LiteralPath $batteryError) { Write-Output (Get-Content -LiteralPath $batteryError -Raw -Encoding UTF8) }
        throw 'Core verification failed; package was not created.'
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start.cmd') -Destination (Join-Path $batteryStage 'Start.cmd')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Stop.cmd') -Destination (Join-Path $batteryStage 'Stop.cmd')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination (Join-Path $batteryStage 'LICENSE')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\USER_GUIDE.zh-CN.txt') -Destination (Join-Path $batteryStage 'README.txt')
    $batteryContents = @('CodexUsageBattery.exe','Start.cmd','Stop.cmd','LICENSE','README.txt') | ForEach-Object { Join-Path $batteryStage $_ }
    $batteryTemporaryZip = Join-Path $batteryStage 'package.zip'
    Write-Output 'Creating the ZIP archive...'
    Compress-Archive -LiteralPath $batteryContents -DestinationPath $batteryTemporaryZip -CompressionLevel Optimal
    $batteryHash = (Get-FileHash -LiteralPath $batteryTemporaryZip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::Copy($batteryTemporaryZip, $batteryZipPath, $false)
    $batteryStream = [IO.File]::Open($batteryHashPath, [IO.FileMode]::CreateNew)
    try {
        $batteryWriter = New-Object IO.StreamWriter($batteryStream, [Text.Encoding]::ASCII)
        try { $batteryWriter.WriteLine($batteryHash + '  ' + $batteryZipName) } finally { $batteryWriter.Dispose() }
    } finally { $batteryStream.Dispose() }
    Write-Output ('Package: ' + $batteryZipPath)
    Write-Output ('SHA-256: ' + $batteryHashPath)
} finally {
    foreach ($batteryName in @('CodexUsageBattery.exe','Start.cmd','Stop.cmd','LICENSE','README.txt','package.zip','last-error.txt')) {
        $batteryTemporaryFile = Join-Path $batteryStage $batteryName
        if (Test-Path -LiteralPath $batteryTemporaryFile -PathType Leaf) { Remove-Item -LiteralPath $batteryTemporaryFile -Force }
    }
    if (Test-Path -LiteralPath $batteryStage -PathType Container) {
        Remove-Item -LiteralPath $batteryStage -Force
    }
}
