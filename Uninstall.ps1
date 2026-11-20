param(
    [string]$AppRoot = $PSScriptRoot
)
$ErrorActionPreference = "Stop"
$AppRoot = [IO.Path]::GetFullPath($AppRoot.TrimEnd('\'))
$ManagerExe = Join-Path $AppRoot "AbioticFactorModManager.exe"
$ManagerAppExe = Join-Path $AppRoot "App\AbioticFactorModManager.exe"
$DataRoot = Join-Path $AppRoot "Data"
$BackupRoot = Join-Path $DataRoot "Backups"
$ProgramDataRoot = [Environment]::GetFolderPath("CommonApplicationData")
$WindowsDirectory = [Environment]::GetFolderPath("Windows")
if ([string]::IsNullOrWhiteSpace($WindowsDirectory)) { $WindowsDirectory = "C:\Windows" }
if (-not (Test-Path -LiteralPath $ManagerExe -PathType Leaf)) {
    throw "Abiotic Factor Mod Manager was not found in: $AppRoot"
}
Set-Location -LiteralPath ([IO.Path]::GetTempPath())

$principal = New-Object Security.Principal.WindowsPrincipal -ArgumentList ([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $elevatedArguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -AppRoot "' + $AppRoot + '"'
$elevatedProcess = Start-Process -FilePath (Join-Path $PSHOME "powershell.exe") -Verb RunAs -ArgumentList $elevatedArguments -WorkingDirectory ([IO.Path]::GetTempPath()) -PassThru
    if ($null -eq $elevatedProcess) { throw "The elevated uninstaller could not be started." }
    Wait-Process -Id $elevatedProcess.Id
    $elevatedProcess.Refresh()
    exit $elevatedProcess.ExitCode
}

function Read-YesNo([string]$Prompt) {
    while ($true) {
        Write-Host -NoNewline ($Prompt + " ")
        $answer = (Read-Host).Trim().ToLowerInvariant()
        if ($answer -eq "y" -or $answer -eq "yes") { return $true }
        if ($answer -eq "n" -or $answer -eq "no") { return $false }
        Write-Host "Please answer y or n."
    }
}

$removeManagerAndModIo = Read-YesNo "Uninstall Abiotic Factor Mod Manager and remove Mod.io mods from game directory? (y/n)"
if (-not $removeManagerAndModIo) {
    Write-Host "Uninstall cancelled."
    exit 0
}
$removeUe4ss = Read-YesNo "Remove UE4SS alongside its mods from game directory? (y/n)"
$removeSteamOptions = Read-YesNo "Remove configured Steam launch options for Abiotic Factor (will restart Steam)? (y/n)"

$runningGame = Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -match '^AbioticFactor($|-Win64-Shipping$)'
}
if ($runningGame) {
    throw "Close Abiotic Factor completely before uninstalling. No game files have been changed."
}
foreach ($process in (Get-Process -Name "AbioticFactorModManager" -ErrorAction SilentlyContinue)) {
    try {
        if ([string]::Equals($process.Path, $ManagerExe, [StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals($process.Path, $ManagerAppExe, [StringComparison]::OrdinalIgnoreCase)) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force }
        }
    } catch { }
}

function Get-ConfiguredGameFilesDirectory {
    $settingsPath = Join-Path $DataRoot "app-settings.json"
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) { return "" }
    $settings = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($settingsPath))
    $configuredPath = [string]$settings.GameDirectory
    if ([string]::IsNullOrWhiteSpace($configuredPath)) { return "" }
    foreach ($candidate in @($configuredPath, (Join-Path $configuredPath "AbioticFactor"))) {
        $shippingExecutable = Join-Path $candidate "Binaries\Win64\AbioticFactor-Win64-Shipping.exe"
        if (Test-Path -LiteralPath $shippingExecutable -PathType Leaf) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    Write-Warning "The saved Abiotic Factor game directory could not be found. Game-side cleanup was skipped."
    return ""
}

function Get-ManagerOwnedModIoIds {
    $ids = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $statePath = Join-Path $DataRoot "installed-mods.json"
    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        try {
            $records = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($statePath))
            foreach ($record in @($records)) {
                $id = [string]$record.Id
                $sourceType = [string]$record.SourceType
                if ($id -match '^\d+$' -and ($sourceType -eq "Mod.io" -or [bool]$record.InstalledByManager)) {
                    [void]$ids.Add($id)
                }
            }
        } catch { Write-Warning "The saved installed-mod list could not be read." }
    }
    if (Test-Path -LiteralPath $BackupRoot -PathType Container) {
        foreach ($directory in (Get-ChildItem -LiteralPath $BackupRoot -Directory -Force)) {
            if ($directory.Name -match '^update_(\d+)_') { [void]$ids.Add($Matches[1]) }
        }
    }
    return @($ids)
}

function Get-SteamRoots {
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($candidate in @("C:\Program Files (x86)\Steam", "C:\Steam", "D:\Steam")) {
        if (Test-Path -LiteralPath $candidate -PathType Container) { $roots.Add([IO.Path]::GetFullPath($candidate)) }
    }
    foreach ($registryPath in @("HKCU:\Software\Valve\Steam", "HKLM:\Software\WOW6432Node\Valve\Steam", "HKLM:\Software\Valve\Steam")) {
        try {
            $key = Get-ItemProperty -LiteralPath $registryPath -ErrorAction Stop
            foreach ($property in @("SteamPath", "InstallPath")) {
                $value = [string]$key.$property
                if (-not [string]::IsNullOrWhiteSpace($value) -and (Test-Path -LiteralPath $value -PathType Container)) {
                    $roots.Add([IO.Path]::GetFullPath($value))
                }
            }
        } catch { }
    }
    return @($roots | Select-Object -Unique)
}

function Get-SteamConfigFiles {
    $files = New-Object System.Collections.Generic.List[string]
    foreach ($root in (Get-SteamRoots)) {
        $userData = Join-Path $root "userdata"
        if (Test-Path -LiteralPath $userData -PathType Container) {
            foreach ($user in (Get-ChildItem -LiteralPath $userData -Directory -ErrorAction SilentlyContinue)) {
                $config = Join-Path $user.FullName "config\localconfig.vdf"
                if (Test-Path -LiteralPath $config -PathType Leaf) { $files.Add($config) }
            }
        }
    }
    return @($files | Select-Object -Unique)
}

function Get-SteamExecutable {
    foreach ($root in (Get-SteamRoots)) {
        $candidate = Join-Path $root "steam.exe"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    foreach ($process in (Get-Process -Name "steam" -ErrorAction SilentlyContinue)) {
        try { if (Test-Path -LiteralPath $process.Path -PathType Leaf) { return $process.Path } } catch { }
    }
    return ""
}

function Find-SteamAppBlock([string]$Text, [int]$FromIndex) {
    $match = [regex]::Match($Text.Substring($FromIndex), '"427410"\s*\{')
    if (-not $match.Success) { return $null }
    $start = $FromIndex + $match.Index
    $openBrace = $Text.IndexOf("{", $start)
    if ($openBrace -lt 0) { return $null }
    $depth = 0
    $insideString = $false
    $escaped = $false
    for ($index = $openBrace; $index -lt $Text.Length; $index++) {
        $character = $Text[$index]
        if ($insideString) {
            if ($escaped) { $escaped = $false; continue }
            if ($character -eq [char]92) { $escaped = $true; continue }
            if ($character -eq '"') { $insideString = $false; continue }
        } else {
            if ($character -eq '"') { $insideString = $true; continue }
            if ($character -eq "{") { $depth++; continue }
            if ($character -eq "}") {
                $depth--
                if ($depth -eq 0) { return [pscustomobject]@{ Start = $start; End = ($index + 1) } }
            }
        }
    }
    return $null
}

function Decode-VdfString([string]$Value) {
    $builder = New-Object System.Text.StringBuilder
    $index = 0
    while ($index -lt $Value.Length) {
        if ($Value[$index] -eq [char]92 -and ($index + 1) -lt $Value.Length) { $index++ }
        [void]$builder.Append($Value[$index])
        $index++
    }
    return $builder.ToString()
}
function Encode-VdfString([string]$Value) { return $Value.Replace("\", "\\").Replace('"', '\"') }

function Get-LaunchOptionsInfo([string]$Text) {
    $range = Find-SteamAppBlock $Text 0
    if ($null -eq $range) { return $null }
    $block = $Text.Substring($range.Start, $range.End - $range.Start)
    $match = [regex]::Match($block, '"LaunchOptions"\s+"(?<value>(?:\\.|[^"\\])*)"')
    if (-not $match.Success) { return $null }
    return [pscustomobject]@{
        BlockStart = $range.Start
        BlockEnd = $range.End
        MatchStart = $match.Index
        MatchLength = $match.Length
        Value = Decode-VdfString $match.Groups["value"].Value
        Block = $block
    }
}

function Strip-ManagedModArguments([string]$Value) {
    $result = [regex]::Replace($Value, '(?i)(^|\s)-Mods=\d+(?:;\d+)*(?=\s|$)', ' ')
    $result = [regex]::Replace($result, '(?i)(^|\s)-SideloadMods(?=\s|$)', ' ')
    return [regex]::Replace($result, '\s+', ' ').Trim()
}
function Get-ArgumentsAfterCommand([string]$Value) {
    $index = $Value.IndexOf("%command%", [StringComparison]::OrdinalIgnoreCase)
    if ($index -lt 0) { return "" }
    return Strip-ManagedModArguments($Value.Substring($index + 9))
}
function Add-AdditionalArguments([string]$Original, [string]$Additional) {
    $original = [string]$Original
    $additional = [string]$Additional
    if ([string]::IsNullOrWhiteSpace($additional)) { return $original.Trim() }
    if ([string]::IsNullOrWhiteSpace($original)) { return $additional.Trim() }
    if ($original.IndexOf($additional.Trim(), [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $original.Trim() }
    return ($original.Trim() + " " + $additional.Trim()).Trim()
}

function Set-SteamLaunchOptions([string]$ConfigPath, [string]$NewValue) {
    $text = [IO.File]::ReadAllText($ConfigPath)
    $info = Get-LaunchOptionsInfo $text
    if ($null -eq $info) { return $false }
    $encoded = Encode-VdfString $NewValue
    $replacement = '"LaunchOptions"' + [string][char]9 + [string][char]9 + '"' + $encoded + '"'
    $newBlock = $info.Block.Substring(0, $info.MatchStart) + $replacement + $info.Block.Substring($info.MatchStart + $info.MatchLength)
    $updatedText = $text.Substring(0, $info.BlockStart) + $newBlock + $text.Substring($info.BlockEnd)

    $backupDirectory = Join-Path $DataRoot "uninstall-restore\Steam"
    New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
    $backupPath = Join-Path $backupDirectory ([IO.Path]::GetFileName($ConfigPath) + "." + (Get-Date -Format "yyyyMMddHHmmssfff") + ".bak")
    Copy-Item -LiteralPath $ConfigPath -Destination $backupPath -Force

    $tempPath = $ConfigPath + ".afmm-temp"
    try {
        [IO.File]::WriteAllText($tempPath, $updatedText, (New-Object System.Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $tempPath -Destination $ConfigPath -Force
    } finally {
        if (Test-Path -LiteralPath $tempPath) { Remove-Item -LiteralPath $tempPath -Force }
    }
    return $true
}

$gameFiles = Get-ConfiguredGameFilesDirectory
if (-not [string]::IsNullOrWhiteSpace($gameFiles)) {
    $modRoot = Join-Path $gameFiles "Mods"
    foreach ($id in (Get-ManagerOwnedModIoIds)) {
        $target = Join-Path $modRoot $id
        if (Test-Path -LiteralPath $target -PathType Container) {
            Remove-Item -LiteralPath $target -Recurse -Force
            Write-Host ("Removed Mod.io mod folder: " + $id)
        }
    }
    if (Test-Path -LiteralPath $modRoot -PathType Container) {
        foreach ($stage in (Get-ChildItem -LiteralPath $modRoot -Directory -Force | Where-Object { $_.Name -like ".stage-*" })) {
            Remove-Item -LiteralPath $stage.FullName -Recurse -Force
        }
        if ((Get-ChildItem -LiteralPath $modRoot -Force | Measure-Object).Count -eq 0) { Remove-Item -LiteralPath $modRoot -Force }
    }

    if ($removeUe4ss) {
        $win64 = Join-Path $gameFiles "Binaries\Win64"
        $ue4ssRoot = Join-Path $win64 "ue4ss"
        $ue4ssLoader = Join-Path $win64 "dwmapi.dll"
        if (Test-Path -LiteralPath $ue4ssRoot -PathType Container) {
            Remove-Item -LiteralPath $ue4ssRoot -Recurse -Force
            Write-Host "Removed UE4SS and its mods."
        }
        if (Test-Path -LiteralPath $ue4ssLoader -PathType Leaf) {
            Remove-Item -LiteralPath $ue4ssLoader -Force
            Write-Host "Removed the UE4SS loader."
        }
    }
}

$steamConfigs = @(Get-SteamConfigFiles)
$wrapperConfigs = @()
foreach ($config in $steamConfigs) {
    $info = Get-LaunchOptionsInfo ([IO.File]::ReadAllText($config))
    if ($null -ne $info -and $info.Value -match '(?i)SteamLaunchWrapper\.ps1') {
        $wrapperConfigs += [pscustomobject]@{ Path = $config; Info = $info }
    }
}

if ($removeSteamOptions -and $wrapperConfigs.Count -gt 0) {
    $steamExecutable = Get-SteamExecutable
    if ([string]::IsNullOrWhiteSpace($steamExecutable)) {
        throw "Steam could not be found. Steam launch options were not changed and the manager folder was left in place."
    }

    if (Get-Process -Name "steam" -ErrorAction SilentlyContinue) {
        Start-Process -FilePath $steamExecutable -ArgumentList "-shutdown" -WorkingDirectory (Split-Path -Parent $steamExecutable) -Wait
        $shutdownDeadline = [DateTime]::UtcNow.AddSeconds(90)
        while ((Get-Process -Name "steam" -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $shutdownDeadline) {
            Start-Sleep -Milliseconds 500
        }
        if (Get-Process -Name "steam" -ErrorAction SilentlyContinue) {
            throw "Steam could not be closed. No success message was displayed; close Steam and run the uninstaller again."
        }
    }

    $originalOptionsPath = Join-Path $DataRoot "Steam\steam-launch-options-original.txt"
    $additionalOptionsPath = Join-Path $DataRoot "Steam\steam-additional-args.txt"
    $hasOriginal = Test-Path -LiteralPath $originalOptionsPath -PathType Leaf
    $savedAdditional = ""
    if (Test-Path -LiteralPath $additionalOptionsPath) { $savedAdditional = [IO.File]::ReadAllText($additionalOptionsPath).Trim() }
    $originalOptions = ""
    if ($hasOriginal) { $originalOptions = [IO.File]::ReadAllText($originalOptionsPath).Trim() }

    foreach ($entry in $wrapperConfigs) {
        $restoreValue = $originalOptions
        if (-not $hasOriginal) { $restoreValue = Get-ArgumentsAfterCommand $entry.Info.Value }
        $restoreValue = Add-AdditionalArguments $restoreValue $savedAdditional
        [void](Set-SteamLaunchOptions $entry.Path $restoreValue)
    }

    Start-Process -FilePath $steamExecutable -WorkingDirectory (Split-Path -Parent $steamExecutable)
    $restartDeadline = [DateTime]::UtcNow.AddSeconds(45)
    while (-not (Get-Process -Name "steam" -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $restartDeadline) {
        Start-Sleep -Milliseconds 500
    }
    if (-not (Get-Process -Name "steam" -ErrorAction SilentlyContinue)) {
        throw "Steam launch options were restored, but Steam did not restart. No success message was displayed."
    }
}
elseif (-not $removeSteamOptions -and $wrapperConfigs.Count -gt 0) {
    # Keep Steam configured, but relocate the wrapper so the entire application directory can be deleted.
    $fallbackDirectory = Join-Path $ProgramDataRoot ("AbioticFactorModManager-SteamFallback-" + [guid]::NewGuid().ToString("N"))
    $fallbackWrapper = Join-Path $fallbackDirectory "SteamLaunchWrapper.ps1"
    Copy-Item -LiteralPath (Join-Path $AppRoot "SteamLaunchWrapper.ps1") -Destination $fallbackWrapper -Force
    $sourceHost = Join-Path $AppRoot "SteamLaunchWrapperHost.exe"
    $fallbackHost = Join-Path $fallbackDirectory "SteamLaunchWrapperHost.exe"
    if (Test-Path -LiteralPath $sourceHost -PathType Leaf) {
        Copy-Item -LiteralPath $sourceHost -Destination $fallbackHost -Force
    }
    [IO.File]::WriteAllText((Join-Path $fallbackDirectory "manager-path.txt"), $ManagerExe, (New-Object System.Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $fallbackDirectory "steam-modio-args.txt"), "", (New-Object System.Text.UTF8Encoding($false)))

    $additionalSource = Join-Path $DataRoot "Steam\steam-additional-args.txt"
    $additionalTarget = Join-Path $fallbackDirectory "steam-additional-args.txt"
    if (Test-Path -LiteralPath $additionalSource) {
        Copy-Item -LiteralPath $additionalSource -Destination $additionalTarget -Force
    } else {
        [IO.File]::WriteAllText($additionalTarget, "", (New-Object System.Text.UTF8Encoding($false)))
    }

    if (Test-Path -LiteralPath $fallbackHost -PathType Leaf) {
        $fallbackCommand = '"' + $fallbackHost + '" "' + $fallbackWrapper + '" %command%'
    }
    else {
        # Backward compatibility for an older installation that predates the hidden Steam host.
        $powershellPath = Join-Path $WindowsDirectory "System32\WindowsPowerShell\v1.0\powershell.exe"
        $fallbackCommand = '"' + $powershellPath + '" -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $fallbackWrapper + '" %command%'
    }
    foreach ($entry in $wrapperConfigs) {
        $trailingArguments = Get-ArgumentsAfterCommand $entry.Info.Value
        $newOptions = $fallbackCommand
        if (-not [string]::IsNullOrWhiteSpace($trailingArguments)) { $newOptions += " " + $trailingArguments }
        [void](Set-SteamLaunchOptions $entry.Path $newOptions)
    }
    Write-Host "Steam launch options were kept using a standalone vanilla fallback wrapper."
}
Write-Host "Removing the Abiotic Factor Mod Manager application folder..."
$tempRoot = [IO.Path]::GetTempPath()
$workerScript = Join-Path $tempRoot ("AFMM-Remove-" + [guid]::NewGuid().ToString("N") + ".ps1")
$completionFile = Join-Path $tempRoot ("AFMM-Remove-" + [guid]::NewGuid().ToString("N") + ".done")
$workerContent = @'
param([string]$TargetRoot, [string]$DoneFile)
Start-Sleep -Milliseconds 750
try {
    if (Test-Path -LiteralPath $TargetRoot) {
        Get-ChildItem -LiteralPath $TargetRoot -Force -Recurse -File -ErrorAction SilentlyContinue |
            ForEach-Object { try { $_.Attributes = [IO.FileAttributes]::Normal } catch { } }
        Remove-Item -LiteralPath $TargetRoot -Recurse -Force -ErrorAction Stop
    }
    if (Test-Path -LiteralPath $TargetRoot) { throw "The application directory still exists after removal." }
    [IO.File]::WriteAllText($DoneFile, "OK")
} catch {
    [IO.File]::WriteAllText($DoneFile, "ERROR: " + $_.Exception.Message)
}
'@
[IO.File]::WriteAllText($workerScript, $workerContent, (New-Object System.Text.UTF8Encoding($false)))
$workerArguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $workerScript + '" -TargetRoot "' + $AppRoot + '" -DoneFile "' + $completionFile + '"'
$worker = Start-Process -FilePath (Join-Path $PSHOME "powershell.exe") -ArgumentList $workerArguments -WorkingDirectory $tempRoot -WindowStyle Hidden -PassThru
if ($null -eq $worker) { throw "The application directory removal worker could not be started." }

$removalDeadline = [DateTime]::UtcNow.AddSeconds(90)
while (-not (Test-Path -LiteralPath $completionFile) -and [DateTime]::UtcNow -lt $removalDeadline) {
    Start-Sleep -Milliseconds 250
}
if (-not (Test-Path -LiteralPath $completionFile)) {
    throw "The application directory removal did not report completion. No success message was displayed."
}
$removalResult = [IO.File]::ReadAllText($completionFile)
Remove-Item -LiteralPath $completionFile -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $workerScript -Force -ErrorAction SilentlyContinue
if ($removalResult -ne "OK" -or (Test-Path -LiteralPath $AppRoot)) {
    throw "The application directory could not be completely removed: $removalResult"
}
Write-Host "Uninstall completed successfully."
