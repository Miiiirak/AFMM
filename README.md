# Abiotic Factor Mod Manager

A self-contained, 64-bit Windows desktop application for managing Abiotic Factor Mod.io sideload packages and UE4SS mods.

## Windows release

1. Open the repository GitHub Releases page and download AbioticFactorModManager-2.0.0-Windows-x64.7z from the latest release.
2. Extract the entire package to its own folder. Keep the executable, DLLs, resource folders, and scripts together.
3. Run AbioticFactorModManager.exe. Windows may request administrator permission because the app writes game files and may update Steam's game configuration.

The release is self-contained and includes its WinUI 3 / Windows App SDK runtime files. The only language satellite included is English (en-us). The matching unbuilt source bundle is also attached to the release as AbioticFactorModManager-2.0.0-Source.zip.

## Application data

The manager creates a Data folder next to the executable. Saved settings, the protected Mod.io API key, installed-mod state, downloads, backups, logs, and Steam wrapper sidecar files are stored there. The app does not need a separate install location or a global user-data folder.

The Steam sidecar files are stored under Data\Steam:
- steam-modio-args.txt contains the current enabled Mod.io IDs and the required -SideloadMods flag.
- steam-additional-args.txt contains the optional arguments saved in Settings.
- SteamLaunchWrapper.ps1 is the wrapper invoked by Steam.
- steam-launch-options-original.txt is the saved pre-manager Steam launch-options value used during uninstall.

## Steam launch options and additional arguments

Use **Set Up Steam** in Settings once to install the required static wrapper command into Abiotic Factor's Steam launch options. The command retains Steam's %command% placeholder and starts the real game executable.

The wrapper reads the latest files in Data\Steam each time the game launches. It appends arguments in this order:

1. Steam's original game arguments.
2. The manager-required Mod.io sideload arguments for enabled Mod.io mods.
3. The optional additional launch arguments saved in the Settings textbox.

Changing enabled Mod.io mods or saving the additional arguments updates the sidecar files; it does not rewrite the Steam launch-options command or require Steam to restart. The textbox is intended for arguments used alongside managed sideload mods. The manager reserves -Mods and -SideloadMods for its own use.

When **Set Up Steam** is confirmed, the manager applies the static wrapper command and restarts Steam. Choosing the clipboard option copies the command without changing Steam's configuration.

## Uninstall

Run Uninstall.bat from the application folder. The uninstaller asks:

- Whether to uninstall the manager and remove its tracked Mod.io mod folders.
- Whether to remove the UE4SS runtime and its mods.
- Whether to remove the configured Steam launch options and restart Steam.

Answering n to the first question cancels the uninstall. The UE4SS and Steam actions are independent choices. When Steam launch options are kept, the uninstaller relocates a vanilla fallback wrapper and the saved additional arguments to ProgramData so Steam no longer depends on the application folder. After a successful uninstall, the entire application folder, including the executable and uninstaller, is removed.

Close Abiotic Factor before uninstalling. If Steam launch options are being removed, Steam must be allowed to close and restart.

## Build from source

The AbioticFactorModManager-Source package includes the WinUI 3 project and Build.ps1.

On a Windows x64 development machine with the .NET 8 SDK and the required Windows App SDK / NuGet packages available:

1. Extract the source package.
2. Close any running copy of the manager.
3. Run Build.ps1 from the extracted project root.

The build publishes a self-contained WinUI 3 application and retains only the English resource satellite.

## Safety

Third-party mod code is not security-audited. Review mods before installing them. The manager does not manage game saves.
