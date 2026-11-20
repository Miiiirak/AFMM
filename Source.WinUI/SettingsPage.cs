using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AbioticFactorModManager;

public sealed class SettingsPage : Page
{
    private const uint MessageBoxYesNo = 0x00000004;
    private const uint MessageBoxQuestionIcon = 0x00000020;
    private const uint MessageBoxDefaultNo = 0x00000100;
    private const int MessageBoxYes = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int ShowWindowsConfirmation(IntPtr owner, string message, string title, uint type);
    private readonly ModManagerService _service;
    private readonly PasswordBox _key = new() { Width = 420, PlaceholderText = "Paste your Mod.io API key", IsPasswordRevealButtonEnabled = true };
    private readonly TextBlock _apiStatus = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _launchOptions = new() { Height = 62, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0), PlaceholderText = "Example: -windowed -ResX=1920 -ResY=1080" };
    private readonly Button _applySteamLaunchOptionsButton = new() { Content = "Set Up Steam" };
    private readonly TextBlock _launchOptionsStatus = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _gamePathText = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
    private readonly TextBlock _gamePathStatus = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly TextBlock _ue4ssStatus = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
    private readonly Button _ue4ssPrimaryButton = new() { Content = "Install UE4SS" };
    private readonly Button _uninstallUe4ssButton = new() { Content = "Uninstall UE4SS", Visibility = Visibility.Collapsed };
    private readonly Button _openModIoModsButton = new() { Content = "Open Mod.io Mods Folder", IsEnabled = false };
    private readonly Button _openUe4ssModsButton = new() { Content = "Open UE4SS Mods Folder", IsEnabled = false };

    public SettingsPage(ModManagerService service)
    {
        _service = service;
        _key.Password = _service.ApiKey;
        _launchOptions.Text = _service.CustomLaunchOptions;
        Content = BuildPage();
        _service.GameSetupChanged += OnGameSetupChanged;
        UpdateGameSetupUi();
    }

    public void ClearLaunchOptionsStatus() => _launchOptionsStatus.Text = "";

    private UIElement BuildPage()
    {
        var sections = new StackPanel { Spacing = 18, HorizontalAlignment = HorizontalAlignment.Stretch };
        var heading = new StackPanel { Spacing = 3 };
        heading.Children.Add(new TextBlock { Text = "Settings", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "Configure your game installation, mod loader, and Mod.io connection.", Opacity = 0.68 });
        sections.Children.Add(heading);

        var api = new StackPanel { Spacing = 12 };
        api.Children.Add(new TextBlock { Text = "API Key", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        api.Children.Add(_key);
        var apiButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var save = new Button { Content = "Save Key", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        save.Click += (_, _) => SaveKey();
        var test = new Button { Content = "Test Connection" };
        test.Click += async (_, _) => await TestConnectionAsync();
        var access = new HyperlinkButton { Content = "Get Free API Key at Mod.io", NavigateUri = new Uri("https://mod.io/me/access") };
        apiButtons.Children.Add(save);
        apiButtons.Children.Add(test);
        apiButtons.Children.Add(access);
        api.Children.Add(apiButtons);
        api.Children.Add(_apiStatus);
        var game = new StackPanel { Spacing = 12 };
        game.Children.Add(new TextBlock { Text = "Game Folder", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        game.Children.Add(new TextBlock
        {
            Text = "The manager checks Steam on first launch. For another installation, choose either AbioticFactor.exe or AbioticFactor-Win64-Shipping.exe.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });
        _gamePathText.Text = _service.HasValidGamePath ? _service.GameDirectory : "--";
        _applySteamLaunchOptionsButton.Visibility = _service.IsSteamGameInstall ? Visibility.Visible : Visibility.Collapsed;
        game.Children.Add(_gamePathText);

        var gameButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var selectExe = new Button
        {
            Content = "Select Game EXE",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        selectExe.Click += (_, _) => SelectGameExecutable();
        gameButtons.Children.Add(selectExe);
        game.Children.Add(gameButtons);
        game.Children.Add(_gamePathStatus);
        sections.Children.Add(CreateSection(game));

        var launch = new StackPanel { Spacing = 10 };
        launch.Children.Add(new TextBlock { Text = "Launch Options", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        launch.Children.Add(new TextBlock
        {
            Text = @"This app manages the launch options for Abiotic Factor to enable sideload mod support and it's best not to mess with that process manually, so here you can enter additional launch arguments that you may usually use. STEAM USERS: If you would like to be able to launch the game through Steam with your managed mods, it is important that you click the ""Set Up Steam"" button to apply the launch options the manager REQUIRES for mod support.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
            HorizontalAlignment = HorizontalAlignment.Stretch
        });
        launch.HorizontalAlignment = HorizontalAlignment.Stretch;
        launch.Children.Add(_launchOptions);
        var saveLaunchOptions = new Button
        {
            Content = "Save Launch Options",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        saveLaunchOptions.Click += (_, _) => SaveLaunchOptions();
        _applySteamLaunchOptionsButton.Click += (_, _) => ApplySteamLaunchOptions();
        var launchButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        launchButtons.Children.Add(saveLaunchOptions);
        launchButtons.Children.Add(_applySteamLaunchOptionsButton);
        launch.Children.Add(launchButtons);
        launch.Children.Add(_launchOptionsStatus);
        var launchSection = CreateSection(launch);
        launchSection.HorizontalAlignment = HorizontalAlignment.Left;
        sections.Children.Add(launchSection);

        sections.Children.Add(CreateSection(api));
        var loader = new StackPanel { Spacing = 12 };
        loader.Children.Add(new TextBlock { Text = "UE4SS", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        loader.Children.Add(new TextBlock
        {
            Text = "Install the Abiotic Factor-specific UE4SS build and configuration from the AF-UE4SS GitHub release.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });
        loader.Children.Add(_ue4ssStatus);

        var loaderButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        _ue4ssPrimaryButton.Click += async (_, _) => await InstallUe4ssAsync();
        _uninstallUe4ssButton.Click += (_, _) => UninstallUe4ss();
        loaderButtons.Children.Add(_ue4ssPrimaryButton);
        loaderButtons.Children.Add(_uninstallUe4ssButton);
        loader.Children.Add(loaderButtons);

        var releaseLink = new HyperlinkButton
        {
            Content = "AF-UE4SS on GitHub",
            NavigateUri = new Uri("https://github.com/igromanru/AF-UE4SS")
        };
        loader.Children.Add(releaseLink);
        sections.Children.Add(CreateSection(loader));

        var folders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 2, 0, 0) };
        _openModIoModsButton.Click += (_, _) => _service.OpenModsFolder();
        _openUe4ssModsButton.Click += (_, _) => _service.OpenUe4ssModsFolder();
        var openBackups = new Button { Content = "Open Backups Folder" };
        openBackups.Click += (_, _) => _service.OpenBackupsFolder();
        folders.Children.Add(_openModIoModsButton);
        folders.Children.Add(_openUe4ssModsButton);
        folders.Children.Add(openBackups);
        sections.Children.Add(folders);

        return new ScrollViewer
        {
            Padding = new Thickness(28, 24, 28, 24),
            Content = sections,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private static Border CreateSection(UIElement content)
    {
        return new Border
        {
            Padding = new Thickness(20),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            MaxWidth = 900,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = content
        };
    }

    private void OnGameSetupChanged()
    {
        DispatcherQueue.TryEnqueue(UpdateGameSetupUi);
    }

    private void UpdateGameSetupUi()
    {
        _gamePathText.Text = _service.HasValidGamePath ? _service.GameDirectory : "--";
        _applySteamLaunchOptionsButton.Visibility = _service.IsSteamGameInstall ? Visibility.Visible : Visibility.Collapsed;
        _gamePathStatus.Text = _service.HasValidGamePath
            ? "Game folder valid and verified."
            : "Game folder not found or invalid.";
        _openModIoModsButton.IsEnabled = _service.HasValidGamePath;
        _openUe4ssModsButton.IsEnabled = _service.HasValidGamePath;
        if (!_service.HasValidGamePath)
        {
            _ue4ssStatus.Text = "Select a valid game executable before installing UE4SS.";
            _ue4ssPrimaryButton.IsEnabled = false;
            _ue4ssPrimaryButton.Content = "Install UE4SS";
            _uninstallUe4ssButton.Visibility = Visibility.Collapsed;
            _uninstallUe4ssButton.IsEnabled = false;
        }
        else if (_service.IsUe4ssInstalled)
        {
            _ue4ssStatus.Text = "UE4SS is installed and was found in this game folder.";
            _ue4ssPrimaryButton.IsEnabled = true;
            _ue4ssPrimaryButton.Content = "Reinstall UE4SS";
            _uninstallUe4ssButton.Visibility = Visibility.Visible;
            _uninstallUe4ssButton.IsEnabled = true;
        }
        else
        {
            _ue4ssStatus.Text = "UE4SS was not found. UE4SS mods cannot be loaded until the loader is installed.";
            _ue4ssPrimaryButton.IsEnabled = true;
            _ue4ssPrimaryButton.Content = "Install UE4SS";
            _uninstallUe4ssButton.Visibility = Visibility.Collapsed;
            _uninstallUe4ssButton.IsEnabled = false;
        }
    }

    private void SelectGameExecutable()
    {
        try
        {
            _gamePathStatus.Text = "Opening the Windows executable picker...";
            var initial = _service.HasValidGamePath ? _service.GameDirectory : _service.SteamRoot;
            var executable = NativeFilePicker.PickSingleExecutable(
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!),
                initial);
            if (string.IsNullOrWhiteSpace(executable))
            {
                _gamePathStatus.Text = "No game executable was selected. The current game folder was left unchanged.";
                return;
            }

            _service.SetGameDirectory(executable);
            UpdateGameSetupUi();
        }
        catch (Exception ex)
        {
            _gamePathStatus.Text = "Could not select the game executable: " + ex.Message;
        }
    }
    private async Task InstallUe4ssAsync()
    {
        if (!_service.HasValidGamePath) return;
        var wasInstalled = _service.IsUe4ssInstalled;
        _ue4ssPrimaryButton.IsEnabled = false;
        _uninstallUe4ssButton.IsEnabled = false;
        _ue4ssPrimaryButton.Content = wasInstalled ? "Reinstalling..." : "Installing...";
        _ue4ssStatus.Text = "Downloading and installing the latest AF-UE4SS package. Existing UE4SS mods will be preserved.";
        try
        {
            var version = await _service.InstallUe4ssRuntimeAsync();
            _ue4ssStatus.Text = "AF-UE4SS " + version + " installed successfully.";
        }
        catch (Exception ex)
        {
            _ue4ssStatus.Text = "UE4SS installation failed: " + ex.Message;
        }
        finally
        {
            UpdateGameSetupUi();
        }
    }

    private void UninstallUe4ss()
    {
        if (!_service.IsUe4ssInstalled) return;
        _ue4ssPrimaryButton.IsEnabled = false;
        _uninstallUe4ssButton.IsEnabled = false;
        _ue4ssStatus.Text = "Backing up and uninstalling the UE4SS runtime...";
        try
        {
            var backup = _service.UninstallUe4ssRuntime();
            _ue4ssStatus.Text = "UE4SS runtime uninstalled. Full backup saved to: " + backup + ". Installed mod folders were retained.";
        }
        catch (Exception ex)
        {
            _ue4ssStatus.Text = "UE4SS uninstall failed: " + ex.Message;
        }
        finally
        {
            UpdateGameSetupUi();
        }
    }

    private async void ApplySteamLaunchOptions()
    {
        try
        {
            var owner = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!);
            var choice = ShowWindowsConfirmation(
                owner,
                "Steam will close completely and reopen to load the updated launch options for Abiotic Factor. Selecting \"No\" will copy the launch options to your clipboard so you can manually apply them in Steam properties.",
                "Restart Steam to apply launch options for Abiotic Factor?",
                MessageBoxYesNo | MessageBoxQuestionIcon | MessageBoxDefaultNo);

            if (choice == MessageBoxYes)
                _launchOptionsStatus.Text = "Steam restarting... (Applying launch options)";
            await _service.ApplySteamLaunchOptionsAsync(choice == MessageBoxYes);
            }
        catch (Exception ex)
        {
            _launchOptionsStatus.Text = "Could not set up Steam launch options: " + ex.Message;
        }
    }
    private async void SaveLaunchOptions()
    {
        try
        {
            _service.SaveLaunchOptions(_launchOptions.Text);
            if (_service.IsSteamGameInstall && !_service.IsSteamWrapperConfigured)
            {
                var owner = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!);
                var choice = ShowWindowsConfirmation(
                    owner,
                    "Your additional launch options have been saved. Steam is not yet configured to use the manager's launch wrapper. Restart Steam now to install the wrapper and apply your saved options? Choosing No copies the required launch command to your clipboard so you can add it manually in Steam properties.",
                    "Set Up Steam to apply saved launch options?",
                    MessageBoxYesNo | MessageBoxQuestionIcon | MessageBoxDefaultNo);
                await _service.ApplySteamLaunchOptionsAsync(choice == MessageBoxYes);
                _launchOptionsStatus.Text = _service.LastLaunchSyncStatus;
            }
            else if (_service.IsSteamGameInstall)
            {
                _launchOptionsStatus.Text = "Additional launch options saved. Steam is already configured; the hidden launch wrapper will use them the next time you start the game.";
            }
            else
            {
                _launchOptionsStatus.Text = "Additional launch options saved successfully. They will be applied when launching the game directly from the manager.";
            }
        }
        catch (Exception ex)
        {
            _launchOptionsStatus.Text = "Launch options were saved, but Steam could not be updated: " + ex.Message;
        }
    }
    private async void SaveKey()
    {
        try
        {
            _service.SaveApiKey(_key.Password);
            _apiStatus.Text = "Key saved. Checking validity...";
            await TestConnectionAsync();
        }
        catch (Exception ex) { _apiStatus.Text = ex.Message; }
    }

    private async Task TestConnectionAsync()
    {
        try
        {
            _apiStatus.Text = "Testing connection...";
            var name = await _service.TestApiKeyAsync();
            _apiStatus.Text = "API key is valid. Connected to " + name + ".";
        }
        catch (Exception ex)
        {
            _apiStatus.Text = ex.Message.Contains("API key", StringComparison.OrdinalIgnoreCase)
                ? "The API key is not correct or valid. Please check it and try again."
                : "Could not test the API key. Please check your internet connection and try again.";
        }
    }
}