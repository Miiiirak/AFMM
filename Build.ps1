$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$source = Join-Path $root "Source.WinUI"
$project = Join-Path $source "AbioticFactorModManager.csproj"
$runtimeId = "win-x64"
$configuration = "Release"
$stage = Join-Path $root "WinUIReleaseCandidate"
$releaseOutput = Join-Path $source "bin\x64\Release\net8.0-windows10.0.19041.0\win-x64"
$backupRoot = Join-Path $root "Data\Backups"

# Give dotnet an explicit cache in environments where USERPROFILE/NUGET_PACKAGES
# may not be propagated to child processes.
if ([string]::IsNullOrWhiteSpace($env:ProgramFiles)) { $env:ProgramFiles = "C:\Program Files" }
if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable("ProgramFiles(x86)"))) {
    [Environment]::SetEnvironmentVariable("ProgramFiles(x86)", "C:\Program Files (x86)", "Process")
}
if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) { $env:NUGET_PACKAGES = Join-Path $env:USERPROFILE ".nuget\packages" }
if (-not (Test-Path $project)) { throw "WinUI project not found: $project" }

$running = Get-CimInstance Win32_Process | Where-Object {
    ($_.Name -eq "AbioticFactorModManager.exe" -and $_.ExecutablePath -like "$root*") -or
    ($_.Name -eq "dotnet.exe" -and $_.CommandLine -like "*$root\AbioticFactorModManager.dll*")
}
if ($running) {
    $ids = ($running | ForEach-Object ProcessId) -join ", "
    throw "Close Abiotic Factor Mod Manager before rebuilding (process ID(s): $ids). No app files were changed."
}

Write-Host "Building WinUI 3 / Windows App SDK $configuration..." -ForegroundColor Cyan
Push-Location $source
try {
    dotnet build $project -c $configuration -p:Platform=x64 -p:WindowsAppSDKSelfContained=true --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "WinUI build failed with exit code $LASTEXITCODE." }

    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    dotnet publish $project -c $configuration -p:Platform=x64 -r $runtimeId --self-contained true -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false -o $stage --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "WinUI publish failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }

# Compile the GUI-subsystem host used to start Steam's PowerShell wrapper without a console flash.
$steamHostSource = Join-Path $root "SteamLaunchWrapperHost.cs"
$steamHostExecutable = Join-Path $stage "SteamLaunchWrapperHost.exe"
$csharpCompiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $steamHostSource)) {
    throw "Steam launch host source is missing: $steamHostSource"
}
if (-not (Test-Path -LiteralPath $csharpCompiler)) {
    throw "The .NET Framework C# compiler was not found: $csharpCompiler"
}
$steamHostIcon = Join-Path $source "Assets\AbioticFactorModManager.ico"
& $csharpCompiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$steamHostExecutable" "/win32icon:$steamHostIcon" $steamHostSource
if ($LASTEXITCODE -ne 0) { throw "Steam launch host compilation failed with exit code $LASTEXITCODE." }

# Include the runtime Steam wrapper, uninstaller, and distribution notes in the portable release payload.
$releaseAssets = @("SteamLaunchWrapper.ps1", "Uninstall.ps1", "Uninstall.bat", "README.md", "PACKAGE-MANIFEST.txt")
foreach ($asset in $releaseAssets) {
    $sourceAsset = Join-Path $root $asset
    if (-not (Test-Path -LiteralPath $sourceAsset -PathType Leaf)) {
        throw "Required release asset is missing: $sourceAsset"
    }
    Copy-Item -LiteralPath $sourceAsset -Destination (Join-Path $stage $asset) -Force
}
# Ship only the English UI resource satellite; all other culture folders are unnecessary for this English-only release.
$cultureDirectories = Get-ChildItem $stage -Directory -Force | Where-Object {
    $_.Name -match '^[a-z]{2,3}(-[A-Za-z0-9]+)+$' -and $_.Name -ine "en-us"
}
foreach ($directory in $cultureDirectories) {
    Remove-Item -LiteralPath $directory.FullName -Recurse -Force
}
Get-ChildItem $stage -File -Filter "*.pdb" -Force | Remove-Item -Force
# Unpackaged WinUI 3 requires the app-generated PRI and compiled XAML resources.
foreach ($asset in @("AbioticFactorModManager.pri", "App.xbf", "MainWindow.xbf")) {
    $from = Join-Path $releaseOutput $asset
    $to = Join-Path $stage $asset
    if (-not (Test-Path $from)) { throw "Required generated XAML resource was not built: $from" }
    Copy-Item $from $to -Force
}

$required = @(
    "AbioticFactorModManager.exe",
    "AbioticFactorModManager.dll",
    "AbioticFactorModManager.deps.json",
    "AbioticFactorModManager.runtimeconfig.json",
    "AbioticFactorModManager.pri",
    "App.xbf",
    "MainWindow.xbf",
    "SteamLaunchWrapperHost.exe",
    "Microsoft.UI.Xaml.Controls.dll",
    "Microsoft.ui.xaml.dll",
    "Microsoft.WindowsAppRuntime.dll"
)
foreach ($file in $required) {
    if (-not (Test-Path (Join-Path $stage $file))) { throw "Release package is incomplete; missing $file" }
}

# Keep a rollback copy of the existing app host and managed entry points.
$existing = @("AbioticFactorModManager.exe", "AbioticFactorModManager.dll",
    "AbioticFactorModManager.deps.json", "AbioticFactorModManager.runtimeconfig.json")
$present = @($existing | Where-Object { Test-Path (Join-Path $root $_) })
if ($present.Count -gt 0) {
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    $backup = Join-Path $backupRoot ("Manager-before-WinUI-" + (Get-Date -Format "yyyyMMdd_HHmmss"))
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    foreach ($file in $present) { Copy-Item (Join-Path $root $file) (Join-Path $backup $file) -Force }
    Write-Host "Previous manager backed up to $backup" -ForegroundColor Yellow
}

Write-Host "Installing the self-contained WinUI 3 manager..." -ForegroundColor Cyan
Get-ChildItem $stage -Force | ForEach-Object {
    Copy-Item $_.FullName $root -Recurse -Force
}

# Remove stale non-English language folders from previous deployments.
Get-ChildItem $root -Directory -Force | Where-Object {
    $_.Name -match '^[a-z]{2,3}(-[A-Za-z0-9]+)+$' -and $_.Name -ine "en-us"
} | Remove-Item -Recurse -Force
if (-not (Test-Path (Join-Path $root "SteamLaunchWrapper.ps1"))) {
    throw "Steam launch wrapper template is missing from the project root."
}

Write-Host ""
Write-Host "WinUI 3 build and promotion complete." -ForegroundColor Green
Get-Item (Join-Path $root "AbioticFactorModManager.exe"),
         (Join-Path $root "AbioticFactorModManager.dll"),
         (Join-Path $root "AbioticFactorModManager.pri") |
    Select-Object Name, Length