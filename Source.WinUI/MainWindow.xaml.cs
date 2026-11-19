using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace AbioticFactorModManager;

public sealed partial class MainWindow : Window
{
    private readonly ModManagerService _service;
    private Page? _browsePage;
    private InstalledModsPage? _installedPage;
    private SettingsPage? _settingsPage;
    private IntPtr _windowHandle;
    private bool _isMaximized;

    private const int GwlStyle = -16;
    private const uint DwmwaUseImmersiveDarkMode = 20;
    private const uint DwmwaBorderColor = 34;
    private const uint DwmwaCaptionColor = 35;

    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsSysMenu = 0x00080000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private const int SwMinimize = 6;
    private const int SwMaximize = 3;
    private const int SwRestore = 9;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, uint attribute, ref int value, uint valueSize);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    public MainWindow()
    {
        InitializeComponent();
        Title = "Abiotic Factor Mod Manager";
        SystemBackdrop = new MicaBackdrop();

        ConfigureCustomWindowChrome();
        Activated += (_, _) => DispatcherQueue.TryEnqueue(ApplyDwmFrameColors);
        CustomTitleBar.Loaded += (_, _) => DispatcherQueue.TryEnqueue(ApplyDwmFrameColors);

        LaunchGameButton.Click += (_, _) => LaunchGame();
        MinimizeWindowButton.Click += (_, _) => MinimizeWindow();
        MaximizeWindowButton.Click += (_, _) => ToggleMaximizeRestore();
        CloseWindowButton.Click += (_, _) => Close();
        TitleBarDragRegion.DoubleTapped += (_, _) => ToggleMaximizeRestore();

        ConfigureCaptionButton(MinimizeWindowButton, isCloseButton: false);
        ConfigureCaptionButton(MaximizeWindowButton, isCloseButton: false);
        ConfigureCaptionButton(CloseWindowButton, isCloseButton: true);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MinimizeWindowButton, "Minimize window");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MaximizeWindowButton, "Maximize or restore window");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CloseWindowButton, "Close window");

        _service = new ModManagerService();
        _service.StatusChanged += message => DispatcherQueue.TryEnqueue(() => Status(message));
        _service.GameSetupChanged += () => DispatcherQueue.TryEnqueue(UpdateGameFolderGate);
        NavView.SelectionChanged += OnNavigationChanged;
        PaneToggleNavigationItem.Tapped += (_, _) => ToggleNavigationPane();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PaneToggleNavigationItem, "Open navigation");
        ToolTipService.SetToolTip(PaneToggleNavigationItem, "Open navigation");
        NavView.PaneOpened += (_, _) => DispatcherQueue.TryEnqueue(() => { UpdatePaneToggleAccessibility(); RefreshContentLayout(); });
        NavView.PaneClosed += (_, _) => DispatcherQueue.TryEnqueue(() => { UpdatePaneToggleAccessibility(); RefreshContentLayout(); });
        OpenGameFolderSettingsButton.Click += (_, _) => NavigateTo("Settings");
        NavView.SelectedItem = NavView.MenuItems[1];
        ShowPage("Browse");
        UpdateGameFolderGate();
    }

    private void ConfigureCustomWindowChrome()
    {
        _windowHandle = WindowNative.GetWindowHandle(this);

        // Extend the client area into the caption area and designate only the left
        // section as draggable; the three custom buttons remain ordinary controls.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        var style = GetWindowLongPtr(_windowHandle, GwlStyle).ToInt64();
        style &= ~WsCaption;
        style |= WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox;
        SetWindowLongPtr(_windowHandle, GwlStyle, new IntPtr(style));

        // Force Windows to recalculate the non-client frame after removing the native caption.
        SetWindowPos(
            _windowHandle,
            IntPtr.Zero,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        ApplyDwmFrameColors();

        _isMaximized = IsZoomed(_windowHandle);
        UpdateMaximizeGlyph();
    }

    private void ApplyDwmFrameColors()
    {
        if (_windowHandle == IntPtr.Zero)
            _windowHandle = WindowNative.GetWindowHandle(this);
        if (_windowHandle == IntPtr.Zero)
            return;

        // DWM may apply its default resize border after the HWND is shown.
        // Set all frame colors after load/activation so windowed mode stays dark.
        var darkMode = 1;
        var frameBorderColor = 0x00181818;
        var captionColor = 0x00181818;

        _ = DwmSetWindowAttribute(_windowHandle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
        _ = DwmSetWindowAttribute(_windowHandle, DwmwaBorderColor, ref frameBorderColor, sizeof(int));
        _ = DwmSetWindowAttribute(_windowHandle, DwmwaCaptionColor, ref captionColor, sizeof(int));
    }
    private void ConfigureCaptionButton(Button button, bool isCloseButton)
    {
        button.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        button.PointerEntered += (_, _) =>
        {
            var color = isCloseButton
                ? Windows.UI.Color.FromArgb(255, 196, 43, 28)
                : Windows.UI.Color.FromArgb(255, 58, 58, 58);
            button.Background = new SolidColorBrush(color);
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        };
    }

    private void MinimizeWindow()
    {
        if (_windowHandle != IntPtr.Zero)
            ShowWindow(_windowHandle, SwMinimize);
    }

    private void ToggleMaximizeRestore()
    {
        if (_windowHandle == IntPtr.Zero) return;

        _isMaximized = IsZoomed(_windowHandle);
        if (_isMaximized)
        {
            ShowWindow(_windowHandle, SwRestore);
            _isMaximized = false;
        }
        else
        {
            ShowWindow(_windowHandle, SwMaximize);
            _isMaximized = true;
        }

        UpdateMaximizeGlyph();
        DispatcherQueue.TryEnqueue(ApplyDwmFrameColors);
    }

    private void UpdateMaximizeGlyph()
    {
        MaximizeWindowGlyph.Glyph = _isMaximized ? "\uE923" : "\uE922";
        ToolTipService.SetToolTip(MaximizeWindowButton, _isMaximized ? "Restore window" : "Maximize window");
    }

    private void ToggleNavigationPane()
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
        UpdatePaneToggleAccessibility();
        RefreshContentLayout();
    }

    private void UpdatePaneToggleAccessibility()
    {
        var name = NavView.IsPaneOpen ? "Close navigation" : "Open navigation";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PaneToggleNavigationItem, name);
        ToolTipService.SetToolTip(PaneToggleNavigationItem, name);
    }
    private void RefreshContentLayout()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ContentFrame.InvalidateMeasure();
            ContentFrame.InvalidateArrange();
            if (ContentFrame.Content is BrowseModsPage browsePage)
                browsePage.RefreshCardLayout();
        });
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag)
            ShowPage(tag);
        UpdateGameFolderGate();
    }

    private void NavigateTo(string tag)
    {
        foreach (NavigationViewItem item in NavView.MenuItems)
            if (item.Tag?.ToString() == tag) { NavView.SelectedItem = item; return; }
        foreach (NavigationViewItem item in NavView.FooterMenuItems)
            if (item.Tag?.ToString() == tag) { NavView.SelectedItem = item; return; }
        ShowPage(tag);
    }

    private void UpdateGameFolderGate()
    {
        var isSettingsPage = NavView.SelectedItem is NavigationViewItem selected &&
            string.Equals(selected.Tag?.ToString(), "Settings", StringComparison.Ordinal);
        var mustBlock = !_service.HasValidGamePath && !isSettingsPage;
        LaunchGameButton.IsEnabled = _service.HasValidGamePath;

        GameFolderOverlay.Visibility = mustBlock ? Visibility.Visible : Visibility.Collapsed;
        ContentFrame.IsHitTestVisible = !mustBlock;
        GameFolderOverlay.IsHitTestVisible = mustBlock;

        if (GameFolderBlurLayer.Background is not AcrylicBrush)
        {
            GameFolderBlurLayer.Background = new AcrylicBrush
            {
                TintColor = Windows.UI.Color.FromArgb(255, 28, 28, 28),
                TintOpacity = 0.72,
                TintLuminosityOpacity = 0.18,
                FallbackColor = Windows.UI.Color.FromArgb(245, 30, 30, 30)
            };
        }

        OpenGameFolderSettingsButton.Style =
            (Style)Application.Current.Resources["AccentButtonStyle"];
    }

    private async void LaunchGame()
    {
        try
        {
            _service.LaunchGame();
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "Could not launch Abiotic Factor",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = ContentFrame.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }
    private void ShowPage(string tag)
    {
        if (string.Equals(tag, "Settings", StringComparison.Ordinal))
            _settingsPage?.ClearLaunchOptionsStatus();

        Page page = tag switch
        {
            "Installed" => _installedPage ??= new InstalledModsPage(_service),
            "Settings" => _settingsPage ??= new SettingsPage(_service),
            _ => _browsePage ??= CreateBrowsePage()
        };

        // Reusing the same page keeps its catalogue cache and controls alive and,
        // importantly, prevents repeated InstalledChanged subscriptions accumulating.
        if (ReferenceEquals(ContentFrame.Content, page))
            return;

        ContentFrame.Content = page;
    }

    private Page CreateBrowsePage()
    {
        var page = new BrowseModsPage(_service);
        page.RequestSettingsNavigation += () => NavigateTo("Settings");
        return page;
    }

    private void Status(string message)
    {
        // Page-owned status text is used for inline feedback; the NavigationView status bar remains uncluttered.
    }
}