# Abiotic Factor Mod Manager 0.0.1

Download the files below from this GitHub Release:

- **AbioticFactorModManager-0.0.1-Windows-x64.7z** - ready-to-run Windows x64 application. Extract the complete archive and launch **AbioticFactorModManager.exe** from the main folder. The application and its runtime files are grouped in the **App** subfolder.
- **AbioticFactorModManager-0.0.1-Source.zip** - the unbuilt WinUI source package.

## Steam launch window fix

Steam now uses a native Windows GUI helper that starts Abiotic Factor directly. The launch path does not start cmd.exe or powershell.exe, so no Command Prompt or PowerShell window should appear while the game is running.

**After updating an existing installation:** close the manager, extract the new Windows archive over the existing manager folder (keep the Data folder), reopen the manager, and click **Set Up Steam** once. Alternatively, click **Save Launch Options** and accept the Steam setup prompt. Restart Steam when prompted. This replaces older Steam launch commands that invoked PowerShell directly or used the previous helper command. Updating the manager files alone does not change Steam's saved launch command.

Your existing settings and mod data remain in Data\ and are not included in the release archive.

See the repository README for installation, mod-management, Steam setup, and uninstall instructions. SHA256SUMS.txt contains checksums for both downloads.