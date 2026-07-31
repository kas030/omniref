[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$publishDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $artifactRoot "OmniRef-$Runtime"))
$archivePath = [System.IO.Path]::GetFullPath(
    (Join-Path $artifactRoot "OmniRef-$Runtime.zip"))

if (-not $publishDirectory.StartsWith(
    $artifactRoot + [System.IO.Path]::DirectorySeparatorChar,
    [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to publish outside the artifacts directory."
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

Push-Location $repositoryRoot
try {
    dotnet restore "OmniRef.slnx"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed with exit code $LASTEXITCODE."
    }

    dotnet test "tests/OmniRef.Tests/OmniRef.Tests.csproj" `
        --configuration $Configuration `
        --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE."
    }

    dotnet publish "src/OmniRef.App/OmniRef.App.csproj" `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        --no-restore `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        --output $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $sampleDirectory = Join-Path $publishDirectory "Samples"
    $documentationDirectory = Join-Path $publishDirectory "Docs"
    New-Item -ItemType Directory -Path $sampleDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $documentationDirectory -Force | Out-Null

    Copy-Item -LiteralPath "README.md" -Destination $publishDirectory
    Copy-Item -LiteralPath "docs/SHORTCUTS.md" -Destination $documentationDirectory
    Copy-Item -LiteralPath "docs/DATA_STORAGE.md" -Destination $documentationDirectory

    $samplePath = Join-Path $sampleDirectory "Welcome.omniref"
    $sampleProcess = Start-Process `
        -FilePath (Join-Path $publishDirectory "OmniRef.exe") `
        -ArgumentList @("--create-sample", "`"$samplePath`"") `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($sampleProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $samplePath)) {
        throw "Could not generate the sample workspace."
    }

    Compress-Archive `
        -Path (Join-Path $publishDirectory "*") `
        -DestinationPath $archivePath `
        -CompressionLevel Optimal
}
finally {
    Pop-Location
}

$archive = Get-Item -LiteralPath $archivePath
Write-Host "Published: $publishDirectory"
Write-Host ("Archive:   {0} ({1:N1} MB)" -f $archive.FullName, ($archive.Length / 1MB))
