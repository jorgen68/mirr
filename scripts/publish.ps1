$ErrorActionPreference = "Stop"

$solutionRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $solutionRoot "src\mirr\mirr.csproj"
$output = Join-Path $solutionRoot "artifacts\publish"
$runtime = if ($env:MIRR_RUNTIME) { $env:MIRR_RUNTIME } else { "win-x64" }

dotnet publish $project `
    -c Release `
    -r $runtime `
    --self-contained $false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $output

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "published to $output"
Get-ChildItem $output | Format-Table Name, Length -AutoSize
