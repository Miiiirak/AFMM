# Abiotic Factor Mod Manager - Source

This is the unbuilt source package for Abiotic Factor Mod Manager 0.0.1.

## Build

On Windows x64 with the .NET 8 SDK and NuGet access:

1. Extract this folder.
2. Run Build.ps1 from the extracted folder.
3. The script builds and publishes the self-contained Windows application and prepares the portable app files.

The resulting app is for Windows x64 and includes English (en-us) resources only.

## Contents

- Source.WinUI contains the application source, project file, manifest, and icon.
- Build.ps1 builds and publishes the WinUI application for local use.
- LaunchApp.cs and LaunchApp.manifest are the source for the lightweight root launcher used in the 0.0.1 portable release.
- SteamLaunchWrapperHost.cs is compiled into the hidden helper that starts the Steam PowerShell wrapper without a visible console.
- SteamLaunchWrapper.ps1 handles Steam launch arguments, including enabled Mod.io sideload flags and saved additional launch options.
- Uninstall.bat launches the interactive uninstaller.
- Uninstall.ps1 handles cleanup of manager files and the selected game/Steam options.
- Data is created by the manager on first run and is not included here.

The Steam launch wrapper runs headlessly. The uninstaller intentionally opens an interactive window because it asks which game and Steam changes should be removed.

Personal settings, API keys, installed-mod lists, game files, build caches, and backups are not included.