param(
    [ValidateSet("Ducky", "Calibrate", "All")]
    [string]$Project = "All",
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Publish-App {
    param(
        [string]$ProjectFolder,
        [string]$OutputName
    )

    $projectPath = Join-Path $root "src/$ProjectFolder/$ProjectFolder.csproj"
    $outputPath = Join-Path $root "publish/$OutputName/$Runtime"

    Write-Host "Publishing $OutputName -> $outputPath"
    dotnet publish $projectPath `
        -c $Configuration `
        -r $Runtime `
        --self-contained `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $outputPath
}

switch ($Project) {
    "Ducky" { Publish-App "Ducky" "Ducky" }
    "Calibrate" { Publish-App "Ducky.Calibrate" "Ducky.Calibrate" }
    default {
        Publish-App "Ducky" "Ducky"
        Publish-App "Ducky.Calibrate" "Ducky.Calibrate"
    }
}

Write-Host "Done."
