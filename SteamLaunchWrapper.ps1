# Steam launch wrapper for Abiotic Factor Mod Manager.
# Steam expands %command% to the actual game executable and its original arguments.
# The manager appends enabled Mod.io sideload flags first, then user-saved additional args.

$ErrorActionPreference = "Stop"
$wrapperDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$argumentsFromSteam = @($args)

if ($argumentsFromSteam.Count -lt 1) { exit 2 }
$gameExecutable = [string]$argumentsFromSteam[0]
if ([string]::IsNullOrWhiteSpace($gameExecutable) -or
    -not (Test-Path -LiteralPath $gameExecutable -PathType Leaf)) { exit 3 }

function ConvertTo-WindowsArgument([string]$value) {
    if ($value.Length -gt 0 -and $value -notmatch '[\s"]') { return $value }
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $backslashes = 0

    foreach ($character in $value.ToCharArray()) {
        if ($character -eq [char]92) { $backslashes++; continue }
        if ($character -eq '"') {
            if ($backslashes -gt 0) {
                [void]$builder.Append([string]::new([char]92, (2 * $backslashes) + 1))
            } else {
                [void]$builder.Append([char]92)
            }
            [void]$builder.Append('"')
        } else {
            if ($backslashes -gt 0) {
                [void]$builder.Append([string]::new([char]92, $backslashes))
            }
            [void]$builder.Append($character)
        }
        $backslashes = 0
    }
    if ($backslashes -gt 0) {
        [void]$builder.Append([string]::new([char]92, 2 * $backslashes))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Get-AdditionalArguments([string]$line) {
    if ([string]::IsNullOrWhiteSpace($line)) { return @() }

    if ($null -eq ("AfmmCommandLineParser" -as [type])) {
        $parserSource = @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class AfmmCommandLineParser
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static string[] Parse(string commandLine)
    {
        int count;
        IntPtr arguments = CommandLineToArgvW(commandLine, out count);
        if (arguments == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            string[] result = new string[count];
            for (int index = 0; index < count; index++)
            {
                IntPtr argument = Marshal.ReadIntPtr(arguments, index * IntPtr.Size);
                result[index] = Marshal.PtrToStringUni(argument);
            }
            return result;
        }
        finally
        {
            LocalFree(arguments);
        }
    }
}
"@
        Add-Type -TypeDefinition $parserSource -ErrorAction Stop
    }

    return @([AfmmCommandLineParser]::Parse($line))
}

$managedArgumentsFile = Join-Path $wrapperDirectory "steam-modio-args.txt"
$managerPathFile = Join-Path $wrapperDirectory "manager-path.txt"
$additionalArgumentsFile = Join-Path $wrapperDirectory "steam-additional-args.txt"
$managedArguments = @()
$additionalArguments = @()

# Mod.io flags are valid only while the manager executable still exists.
try {
    if ((Test-Path -LiteralPath $managerPathFile -PathType Leaf) -and
        (Test-Path -LiteralPath $managedArgumentsFile -PathType Leaf)) {
        $managerExecutable = [IO.File]::ReadAllText($managerPathFile).Trim()
        if (-not [string]::IsNullOrWhiteSpace($managerExecutable) -and
            (Test-Path -LiteralPath $managerExecutable -PathType Leaf)) {
            $managedLine = [IO.File]::ReadAllText($managedArgumentsFile).Trim()
            if (-not [string]::IsNullOrWhiteSpace($managedLine)) {
                $tokens = @($managedLine -split '\s+' | Where-Object { $_ })
                $valid = $tokens.Count -gt 0
                foreach ($token in $tokens) {
                    if ($token -notmatch '^-Mods=\d+(;\d+)*$' -and $token -ne '-SideloadMods') {
                        $valid = $false
                        break
                    }
                }
                if ($valid) { $managedArguments = $tokens }
            }
        }
    }
} catch {
    $managedArguments = @()
}

# Additional launch options remain useful even when a vanilla fallback wrapper is retained.
try {
    if (Test-Path -LiteralPath $additionalArgumentsFile -PathType Leaf) {
        $additionalLine = [IO.File]::ReadAllText($additionalArgumentsFile).Trim()
        $additionalArguments = @(Get-AdditionalArguments $additionalLine)
    }
} catch {
    # Invalid optional arguments never suppress the required Mod.io flags.
    $additionalArguments = @()
}

$quotedArguments = New-Object System.Collections.Generic.List[string]
for ($index = 1; $index -lt $argumentsFromSteam.Count; $index++) {
    $quotedArguments.Add((ConvertTo-WindowsArgument ([string]$argumentsFromSteam[$index])))
}
foreach ($argument in $managedArguments) {
    $quotedArguments.Add((ConvertTo-WindowsArgument ([string]$argument)))
}
foreach ($argument in $additionalArguments) {
    $quotedArguments.Add((ConvertTo-WindowsArgument ([string]$argument)))
}

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $gameExecutable
$startInfo.WorkingDirectory = Split-Path -Parent $gameExecutable
$startInfo.UseShellExecute = $false
$startInfo.Arguments = [string]::Join(" ", $quotedArguments)

try {
    $gameProcess = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $gameProcess) { exit 4 }
    $gameProcess.WaitForExit()
    $exitCode = $gameProcess.ExitCode
    $gameProcess.Dispose()
    exit $exitCode
}
catch {
    # If any manager-added argument prevents launch, retry with Steam's original arguments only.
    $baseArguments = New-Object System.Collections.Generic.List[string]
    for ($index = 1; $index -lt $argumentsFromSteam.Count; $index++) {
        $baseArguments.Add((ConvertTo-WindowsArgument ([string]$argumentsFromSteam[$index])))
    }
    $startInfo.Arguments = [string]::Join(" ", $baseArguments)
    try {
        $fallback = [System.Diagnostics.Process]::Start($startInfo)
        if ($null -ne $fallback) {
            $fallback.WaitForExit()
            $exitCode = $fallback.ExitCode
            $fallback.Dispose()
            exit $exitCode
        }
    } catch { }
    exit 5
}
