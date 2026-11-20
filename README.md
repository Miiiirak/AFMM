# Abiotic Factor Mod Manager

**Abiotic Factor Mod Manager** helps you manage both major kinds of Abiotic Factor mods in one place:

- **Mod.io sideload mods** - browse and install sideload-ready Mod.io mods, then choose which ones are active.
- **UE4SS mods** - install and manage UE4SS mods alongside your Mod.io sideload mods.

You can use both types together. The manager keeps their install and launch setup organized so you do not have to manage mod folders and launch arguments by hand.

## Install

There is no installer or setup wizard. Installing the manager is just extracting the download.

1. Open the [Releases page](https://github.com/Miiiirak/AFMM/releases) and download **AbioticFactorModManager-0.0.1-Windows-x64.7z**.
2. Extract the **entire** archive into a folder of your choice. For example, you can put it in Documents\Games\Abiotic Factor Mod Manager or another location you prefer.
3. Open that folder and run **AbioticFactorModManager.exe**.
4. Approve the Windows administrator prompt. The manager needs permission to install and manage game mods and, when requested, update Abiotic Factor's Steam launch options.
5. On the first run, check the **Settings** page and make sure the correct Abiotic Factor game folder is selected. Add your Mod.io API key there if you want to browse Mod.io mods.

Keep the files together in the extracted folder. The main folder contains the launcher and setup files; the actual application and its required runtime files are grouped in the App subfolder. Do not move the EXE by itself or try to run it from inside the compressed archive.

## Install and manage mods

1. Open **Browse Mods** in the manager.
2. Find a mod you want and use its install option. The manager supports Mod.io sideload mods and UE4SS mods.
3. Open **Installed Mods** to see what is installed and turn mods on or off.
4. You can have Mod.io sideload mods and UE4SS mods installed at the same time.

For Steam players: click **Save Launch Options** after entering any extra arguments. If Steam is not set up yet (or still uses the older launch wrapper), the manager will offer to install its Steam launch setup and restart Steam. You can also use **Set Up Steam** manually at any time. Steam must use the manager's launch setup to start Abiotic Factor with your enabled Mod.io sideload mods. Once Steam is configured, later saves update the options used on the next launch. The Steam command now uses a native Windows GUI helper that starts the game directly; it does not start cmd.exe or powershell.exe. After updating the manager files, open the manager and click Set Up Steam once (or save Launch Options and accept the Steam setup prompt) to replace any older Steam command that starts PowerShell directly. Restart Steam when prompted. Merely replacing the application files does not change Steam's saved launch command.

## Uninstall

1. Close Abiotic Factor.
2. Open the folder where you extracted the manager.
3. Double-click **Uninstall.bat**.
4. Answer the three questions:
   - **Uninstall the manager and remove Mod.io mods from the game folder?** Choose y to remove the manager and its tracked Mod.io mod folders. Choosing n cancels the uninstall.
   - **Remove UE4SS and its mods from the game folder?** Choose y to remove UE4SS and its installed mods, or n to leave them in place.
   - **Remove Abiotic Factor's configured Steam launch options?** Choose y to restore the previous Steam launch options and restart Steam, or n to keep Steam configured with a standalone fallback that no longer loads the manager's mods.

When the uninstall finishes successfully, the manager's extracted folder is deleted too, including the EXE and Uninstall.bat. Your answers to the UE4SS and Steam questions decide whether those game/Steam changes are removed or left in place.

## Source code

The unbuilt source package is available as **AbioticFactorModManager-0.0.1-Source.zip** on the Releases page. It includes the WinUI project and the scripts needed to build the manager on Windows.