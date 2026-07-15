# FullVid Extension Packaging Script
# Creates a .pext package for Playnite installation.
#
# Usage: .\package_extension.ps1 [-Configuration Release|Debug]
#
# Note: This script packages an already-built project. Build first with:
#   dotnet build -c Release

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  FullVid Extension Packaging" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration" -ForegroundColor Yellow
Write-Host ""

# Get project root (one level up from scripts/)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir
Set-Location $projectRoot

# Read version from version.txt (single source of truth)
$versionFile = Join-Path $projectRoot "version.txt"
if (-not (Test-Path $versionFile)) {
    Write-Host "ERROR: version.txt not found. Please create it with the version number (e.g., 0.1.0)" -ForegroundColor Red
    exit 1
}
$versionFull = (Get-Content $versionFile -Raw).Trim()
# Convert version format: 0.1.0 -> 0_1_0 for filename
$version = $versionFull -replace '\.', '_'

# Build paths
$outputDir = "src\bin\$Configuration\net4.6.2"
$packageDir = "package"

# Verify main DLL exists and show details
$dllPath = Join-Path $outputDir "FullVid.dll"
if (-not (Test-Path $dllPath)) {
    Write-Host "ERROR: FullVid.dll not found in $outputDir" -ForegroundColor Red
    Write-Host ""
    Write-Host "Please build the project first:" -ForegroundColor Yellow
    Write-Host "  dotnet build -c $Configuration" -ForegroundColor White
    Write-Host ""
    exit 1
}

$dllInfo = Get-Item $dllPath
Write-Host "Found DLL: $($dllInfo.Name)" -ForegroundColor Green
Write-Host "  Size: $([math]::Round($dllInfo.Length/1KB, 2)) KB" -ForegroundColor Gray
Write-Host "  Modified: $($dllInfo.LastWriteTime)" -ForegroundColor Gray
Write-Host ""

# Clean previous package
Write-Host "Preparing package directory..." -ForegroundColor Yellow
if (Test-Path $packageDir) {
    Remove-Item -Path $packageDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  Cleaned existing package directory" -ForegroundColor Gray
}
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

# Stamp extension.yaml with current version before copying
$extensionYamlPath = Join-Path $projectRoot "extension.yaml"
if (Test-Path $extensionYamlPath) {
    $yamlContent = Get-Content $extensionYamlPath -Raw
    if ($yamlContent -match "Version:\s*[\d\.]+") {
        $yamlContent = $yamlContent -replace "Version:\s*[\d\.]+", "Version: $versionFull"
        Set-Content -Path $extensionYamlPath -Value $yamlContent -NoNewline
        Write-Host "  Stamped extension.yaml with version $versionFull" -ForegroundColor Gray
    }
}

Write-Host "Copying extension files..." -ForegroundColor Yellow

# Copy core files (extension.yaml + icon.png required; LICENSE/NOTICES optional)
$coreFiles = @(
    "extension.yaml",
    "icon.png",
    "LICENSE",
    "NOTICES.txt"
)
foreach ($file in $coreFiles) {
    if (Test-Path $file) {
        Copy-Item $file -Destination $packageDir -Force
        Write-Host "  Copied file: $file" -ForegroundColor Gray
    } else {
        Write-Host "  WARNING: $file not found (optional)" -ForegroundColor Yellow
    }
}

# Copy main DLL
Copy-Item $dllPath -Destination $packageDir -Force
Write-Host "  Copied: FullVid.dll" -ForegroundColor Gray

# Copy WebView2 native loader tree (runtimes/win-*/native/WebView2Loader.dll).
# Without this in the correct relative path the WebView2 control fails to
# initialize at runtime and the Watch flow is dead. Copy the whole tree.
$runtimesSrc = Join-Path $outputDir "runtimes"
if (Test-Path $runtimesSrc) {
    Copy-Item $runtimesSrc -Destination (Join-Path $packageDir "runtimes") -Recurse -Force
    Write-Host "  Copied: runtimes\ (WebView2 native loader tree)" -ForegroundColor Gray
} else {
    Write-Host "  ERROR: runtimes\ folder not found in build output. WebView2 will fail to load." -ForegroundColor Red
    exit 1
}

# Copy dependency DLLs from build output. Sweep every *.dll except the main
# assembly and the ones Playnite/WPF already provide. This catches the WebView2
# managed trio, MaterialDesignThemes/Colors, Microsoft.Xaml.Behaviors,
# Newtonsoft.Json, and anything else the build actually pulls in.
Write-Host "Copying dependencies from build output..." -ForegroundColor Yellow
$depDlls = Get-ChildItem -Path $outputDir -Filter "*.dll" | Where-Object {
    $_.Name -ne "FullVid.dll" -and
    $_.Name -ne "Playnite.SDK.dll" -and
    $_.Name -notlike "System.*" -and
    $_.Name -ne "WindowsBase.dll" -and
    $_.Name -ne "PresentationCore.dll" -and
    $_.Name -ne "PresentationFramework.dll"
}
foreach ($dll in $depDlls) {
    $destPath = Join-Path $packageDir $dll.Name
    if (-not (Test-Path $destPath)) {
        Copy-Item $dll.FullName -Destination $destPath -Force
        Write-Host "  Copied: $($dll.Name)" -ForegroundColor Gray
    }
}

# Verify required files are present before archiving
Write-Host "Verifying package contents..." -ForegroundColor Yellow
$missing = @()
$requiredRelative = @(
    "FullVid.dll",
    "extension.yaml",
    "icon.png",
    "runtimes\win-x86\native\WebView2Loader.dll"
)
foreach ($rel in $requiredRelative) {
    if (-not (Test-Path (Join-Path $packageDir $rel))) {
        $missing += $rel
    }
}
if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "ERROR: Missing required files in package:" -ForegroundColor Red
    foreach ($m in $missing) { Write-Host "  - $m" -ForegroundColor Red }
    exit 1
}

$packageFiles = Get-ChildItem -Path $packageDir -File -Recurse
Write-Host "  Package contains $($packageFiles.Count) files" -ForegroundColor Gray
Write-Host ""

# Create pext output folder
$pextOutputDir = Join-Path $projectRoot "pext"
if (-not (Test-Path $pextOutputDir)) {
    New-Item -ItemType Directory -Path $pextOutputDir -Force | Out-Null
}

$pextFileName = "FullVid-$version.pext"
$pextFilePath = Join-Path $pextOutputDir $pextFileName
$zipFilePath = Join-Path $pextOutputDir "FullVid-$version.zip"

if (Test-Path $pextFilePath) { Remove-Item $pextFilePath -Force -ErrorAction SilentlyContinue }
if (Test-Path $zipFilePath) { Remove-Item $zipFilePath -Force -ErrorAction SilentlyContinue }

# Create ZIP then rename to .pext (Compress-Archive won't write a .pext directly).
# NOTE: Compress-Archive can fail if Windows Defender is mid-scan on the freshly
# built DLLs ("file in use"/access error). If that happens, retry the package step.
Write-Host "Creating .pext archive..." -ForegroundColor Yellow
try {
    Compress-Archive -Path "$packageDir\*" -DestinationPath $zipFilePath -Force
    Rename-Item -Path $zipFilePath -NewName $pextFileName -Force
    $packageInfo = Get-Item $pextFilePath

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  PACKAGE CREATED SUCCESSFULLY!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "Package Details:" -ForegroundColor Cyan
    Write-Host "  File: $($packageInfo.Name)" -ForegroundColor White
    Write-Host "  Size: $([math]::Round($packageInfo.Length/1KB, 2)) KB" -ForegroundColor White
    Write-Host "  Location: $pextOutputDir" -ForegroundColor White
    Write-Host "  Version: $versionFull" -ForegroundColor White
    Write-Host ""
    Write-Host "Package Contents:" -ForegroundColor Cyan
    foreach ($file in $packageFiles | Sort-Object FullName) {
        $rel = $file.FullName.Substring((Resolve-Path $packageDir).Path.Length + 1)
        Write-Host "  - $rel ($([math]::Round($file.Length/1KB, 2)) KB)" -ForegroundColor Gray
    }
    Write-Host ""
    Write-Host "To install in Playnite:" -ForegroundColor Cyan
    Write-Host "  1. Open Playnite" -ForegroundColor White
    Write-Host "  2. Go to Add-ons -> Extensions" -ForegroundColor White
    Write-Host "  3. Click 'Add extension' and select the .pext file" -ForegroundColor White
    Write-Host ""
} catch {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Red
    Write-Host "  ERROR: Failed to create package" -ForegroundColor Red
    Write-Host "========================================" -ForegroundColor Red
    Write-Host ""
    Write-Host "Error Details:" -ForegroundColor Yellow
    Write-Host $_.Exception.Message -ForegroundColor Red
    if ($_.Exception.InnerException) {
        Write-Host $_.Exception.InnerException.Message -ForegroundColor Red
    }
    Write-Host ""
    exit 1
}
