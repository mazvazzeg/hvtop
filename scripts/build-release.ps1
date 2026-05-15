param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$nativeProject = Join-Path $repoRoot "hvtop.Native\hvtop.Native.csproj"
$rdcProject = Join-Path $repoRoot "hvtop.Rdc\hvtop.Rdc.csproj"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content -LiteralPath $nativeProject
    $Version = $projectXml.Project.PropertyGroup.Version
}

$artifactsRoot = Join-Path $repoRoot "artifacts"
$stageRoot = Join-Path $artifactsRoot "stage"
$releaseRoot = Join-Path $artifactsRoot "release"

New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}

function Invoke-Publish {
    param(
        [string]$Project,
        [string]$Output,
        [bool]$SelfContained,
        [bool]$Trimmed
    )

    $args = @(
        "publish", $Project,
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", $SelfContained.ToString().ToLowerInvariant(),
        "-o", $Output,
        "/p:PublishSingleFile=true",
        "/p:EnableCompressionInSingleFile=$($SelfContained.ToString().ToLowerInvariant())",
        "/p:PublishReadyToRun=false",
        "/p:DebugType=none",
        "/p:DebugSymbols=false",
        "/p:PublishTrimmed=$($Trimmed.ToString().ToLowerInvariant())"
    )

    if ($NoRestore) {
        $args += "--no-restore"
    }

    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project"
    }
}

function New-HvtopZip {
    param(
        [bool]$Portable
    )

    $variant = if ($Portable) { "portable" } else { "framework-dependent" }
    $zipBase = if ($Portable) { "hvtop-$Version-$Runtime-portable" } else { "hvtop-$Version-$Runtime" }
    $zipPath = Join-Path $releaseRoot "$zipBase.zip"
    $variantRoot = Join-Path $stageRoot $zipBase
    $nativeOut = Join-Path $variantRoot "publish-hvtop"
    $rdcOut = Join-Path $variantRoot "publish-rdc"
    $zipStage = Join-Path $variantRoot "zip"

    New-Item -ItemType Directory -Force -Path $nativeOut, $rdcOut, $zipStage | Out-Null

    Write-Host "Publishing $variant hvtop..."
    Invoke-Publish -Project $nativeProject -Output $nativeOut -SelfContained:$Portable -Trimmed:$Portable

    Write-Host "Publishing $variant hvtop-rdc..."
    Invoke-Publish -Project $rdcProject -Output $rdcOut -SelfContained:$Portable -Trimmed:$Portable

    Copy-Item -LiteralPath (Join-Path $nativeOut "hvtop.exe") -Destination (Join-Path $zipStage "hvtop.exe") -Force
    Copy-Item -LiteralPath (Join-Path $rdcOut "hvtop-rdc.exe") -Destination (Join-Path $zipStage "hvtop-rdc.exe") -Force

    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -LiteralPath (Join-Path $zipStage "hvtop.exe"), (Join-Path $zipStage "hvtop-rdc.exe") -DestinationPath $zipPath -Force
    Write-Host "Created $zipPath"
}

New-HvtopZip -Portable:$false
New-HvtopZip -Portable:$true
