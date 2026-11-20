# Abiotic Factor Mod Manager - Source

This is the unbuilt source package for Abiotic Factor Mod Manager 0.0.1.

## Build

On Windows x64 with the .NET 8 SDK and NuGet access:

1. Extract this folder.
2. Run Build.ps1 from the extracted folder.
3. The script builds and publishes the self-contained Windows application into the project folder.

The resulting app is for Windows x64 and includes English (en-us) resources only.

## Contents

- Source.WinUI contains the application source, project file, manifest, and icon.
- Build.ps1 builds and publishes the WinUI application for local use.
- LaunchApp.cs and LaunchApp.manifest are the source for the small root launcher used in the 0.0.1 release archive.
- SteamLaunchWrapper.ps1 supports Steam launch setup, including the additional launch options saved in Settings.
- Uninstall.bat launches the uninstaller.
- Uninstall.ps1 handles cleanup of manager files and the selected game/Steam options.
- Data is created by the manager on first run and is not included here.

Personal settings, API keys, installed-mod lists, game files, build caches, and backups are not included.