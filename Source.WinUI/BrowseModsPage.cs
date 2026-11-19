using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AbioticFactorModManager;

public sealed class BrowseModsPage : Page
{
    private readonly ModManagerService _service;
    private readonly ObservableCollection<BrowseMod> _mods = new();
    private readonly TextBox _search = new() { PlaceholderText = "Search Abiotic Factor mods...", MinWidth = 320 };
    private readonly ComboBox _sort = new() { Width = 170, SelectedIndex = 0 };
    private readonly GridView _grid = new() { IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _resultLabel = new() { Opacity = 0.72, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _previous = new() { Content = "Previous", IsEnabled = false };
    private readonly Button _next = new() { Content = "Next", IsEnabled = false };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.72 };
    private readonly TextBlock _zipStatus = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
    private TextBlock? _ue4ssZipWarning;
    private Button? _chooseZipButton;
    private int _page = 1;
    private int _total;
    private bool _busy;
    private bool _startupLoadHandled;
    private Grid? _catalogueLayout;
    private Border? _keyNotice;
    private TextBlock? _keyNoticeText;
    private UIElement[] _catalogueControls = Array.Empty<UIElement>();

    public event Action? RequestSettingsNavigation;

    public BrowseModsPage(ModManagerService service)
    {
        _service = service;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Content = BuildPage();
        _service.InstalledChanged += UpdateInstalledLabels;
        _service.GameSetupChanged += UpdateZipInstallState;
        _service.ApiKeyChanged += OnApiKeyChanged;
        Loaded += async (_, _) =>
        {
            if (_startupLoadHandled) return;
            _startupLoadHandled = true;

            if (_service.HasApiKey && !_service.IsApiKeyValid)
            {
                try { await _service.TestApiKeyAsync(); }
                catch { /* Keep the locked notice visible if validation fails. */ }
            }

            UpdateCatalogueAccess();
            if (_service.IsApiKeyValid && _mods.Count == 0)
                await SearchAsync();
        };
    }

    private void OnApiKeyChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateCatalogueAccess();
            if (_service.IsApiKeyValid && _mods.Count == 0)
                _ = SearchAsync(forceRefresh: true);
        });
    }
    private UIElement BuildPage()
    {
        var root = new Grid { Padding = new Thickness(28, 20, 28, 24), RowSpacing = 16, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new StackPanel { Spacing = 3 };
        heading.Children.Add(new TextBlock { Text = "Browse Mods", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "Find, inspect, and install Abiotic Factor mods.", Opacity = 0.68 });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var tabs = new TabView { IsAddTabButtonVisible = false, CanDragTabs = false, TabWidthMode = TabViewWidthMode.SizeToContent, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        var browse = new TabViewItem { Header = "Mod.io catalogue", IsClosable = false, IconSource = new SymbolIconSource { Symbol = Symbol.Find } };
        browse.Content = BuildCatalogue();
        var link = new TabViewItem { Header = "ZIP install", IsClosable = false, IconSource = new SymbolIconSource { Symbol = Symbol.Download } };
        link.Content = BuildLinkInstall();
        tabs.TabItems.Add(browse);
        tabs.TabItems.Add(link);
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);
        return root;
    }

    private UIElement BuildCatalogue()
    {
        var layout = new Grid { RowSpacing = 14, Padding = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        _catalogueLayout = layout;
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _keyNotice = new Border
        {
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]
        };
        var noticeContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        _keyNoticeText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        noticeContent.Children.Add(_keyNoticeText);
        var settingsButton = new Button { Content = "Open Settings", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        settingsButton.Click += (_, _) => RequestSettingsNavigation?.Invoke();
        noticeContent.Children.Add(settingsButton);
        _keyNotice.Child = noticeContent;
        Grid.SetRow(_keyNotice, 0);
        layout.Children.Add(_keyNotice);

        var tools = new Grid { ColumnSpacing = 10, VerticalAlignment = VerticalAlignment.Center };
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _search.Width = 420;
        _search.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                await SearchFromInputAsync();
            }
        };
        var searchControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var searchButton = new Button { Content = "Search", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        searchButton.Click += async (_, _) => await SearchFromInputAsync();
        var refreshButton = new Button { Content = "Refresh" };
        refreshButton.Click += async (_, _) =>
        {
            _page = 1;
            await SearchAsync(forceRefresh: true);
        };
        searchControls.Children.Add(_search);
        searchControls.Children.Add(searchButton);
        searchControls.Children.Add(refreshButton);
        Grid.SetColumn(searchControls, 0);
        tools.Children.Add(searchControls);

        _sort.Items.Add("Recently updated");
        _sort.Items.Add("Most downloads");
        _sort.Items.Add("Name A-Z");
        _sort.SelectionChanged += async (_, _) => { _page = 1; await SearchAsync(forceRefresh: true); };
        var sortControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        sortControls.Children.Add(new TextBlock { Text = "Sort by", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
        sortControls.Children.Add(_sort);
        Grid.SetColumn(sortControls, 1);
        tools.Children.Add(sortControls);
        Grid.SetRow(tools, 1);
        layout.Children.Add(tools);

        var info = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        info.Children.Add(_resultLabel);
        Grid.SetRow(info, 2);
        layout.Children.Add(info);

        _grid.Margin = new Thickness(0);
        _grid.ItemsSource = _mods;
        layout.SizeChanged += (_, e) =>
        {
            _grid.Width = e.NewSize.Width;
            UpdateCardWidths(e.NewSize.Width);
        };
        _grid.ItemTemplate = CreateModTemplate();
        _grid.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("""
<ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <ItemsWrapGrid Orientation="Horizontal" MaximumRowsOrColumns="6"/>
</ItemsPanelTemplate>
""");
        _grid.ItemClick += async (_, e) => { if (e.ClickedItem is BrowseMod mod) await ShowDetailsAsync(mod); };
        _grid.HorizontalAlignment = HorizontalAlignment.Stretch;
        _grid.VerticalAlignment = VerticalAlignment.Stretch;
        ScrollViewer.SetVerticalScrollBarVisibility(_grid, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_grid, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollMode(_grid, ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollMode(_grid, ScrollMode.Disabled);
        Grid.SetRow(_grid, 3);
        layout.Children.Add(_grid);

        var pagination = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 14 };
        _previous.Click += async (_, _) => await NavigateToPageAsync(_page - 1);
        _next.Click += async (_, _) => await NavigateToPageAsync(_page + 1);
        pagination.Children.Add(_previous);
        pagination.Children.Add(_pageLabel);
        pagination.Children.Add(_next);
        Grid.SetRow(pagination, 4);
        layout.Children.Add(pagination);

        _catalogueControls = new UIElement[] { tools, info, _grid, pagination };
        UpdateCatalogueAccess();
        return layout;
    }

    public void RefreshCardLayout()
    {
        UpdateCardWidths(_grid.ActualWidth);
        _grid.InvalidateMeasure();
        _grid.InvalidateArrange();
    }
    private void UpdateCatalogueAccess()
    {
        bool valid = _service.IsApiKeyValid;
        if (_keyNotice != null)
        {
            _keyNotice.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
            if (_keyNoticeText != null)
            {
                _keyNoticeText.Text = !_service.HasApiKey
                    ? "A Mod.io API key is required to browse and search the catalogue. Add your key in Settings to continue."
                    : "The API key is not correct or valid. Please check it and try again.";
            }
        }

        foreach (var control in _catalogueControls)
            control.Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateCardWidths(double availableWidth)
    {
        if (availableWidth <= 0 || double.IsNaN(availableWidth) || double.IsInfinity(availableWidth))
            return;

        const double cardMargin = 12;
        const double verticalScrollbarAllowance = 24;
        double usableWidth = Math.Max(0, availableWidth - verticalScrollbarAllowance);
        const double preferredSlotWidth = 300;
        const double maxCardWidth = 360;
        const double minCardWidth = 220;

        int columns = Math.Max(1, (int)Math.Floor((usableWidth + cardMargin) / preferredSlotWidth));
        columns = Math.Min(columns, 6);

        // When there is room, add columns rather than letting cards grow too wide.
        while (columns < 6 &&
               usableWidth / columns - cardMargin > maxCardWidth &&
               usableWidth / (columns + 1) - cardMargin >= minCardWidth)
        {
            columns++;
        }

        double cardWidth = Math.Max(minCardWidth, usableWidth / columns - cardMargin);
        double thumbnailHeight = cardWidth * 0.52; // Consistent wide thumbnail ratio.

        foreach (var mod in _mods)
        {
            mod.CardWidth = cardWidth;
            mod.ThumbnailHeight = thumbnailHeight;
        }
    }
    private static DataTemplate CreateModTemplate()
    {
        const string xaml = """
<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Border Width="{Binding CardWidth}" MinHeight="300" Padding="10" Margin="6" CornerRadius="10" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1">
    <StackPanel Spacing="7">
      <Image Source="{Binding Thumbnail}" Height="{Binding ThumbnailHeight}" HorizontalAlignment="Stretch" Stretch="UniformToFill"/>
      <TextBlock Text="{Binding Name}" FontSize="16" FontWeight="SemiBold" TextWrapping="Wrap" MaxLines="2"/>
      <TextBlock Text="{Binding Summary}" Opacity="0.72" TextWrapping="Wrap" MaxLines="2"/>
      <TextBlock Text="{Binding CreatorText}" FontSize="12" Opacity="0.68"/>
      <TextBlock Text="{Binding DownloadsText}" FontSize="12" Opacity="0.68"/>
      <TextBlock Text="{Binding InstalledText}" FontSize="12" Foreground="{ThemeResource SystemFillColorSuccessBrush}" TextWrapping="Wrap"/>
    </StackPanel>
  </Border>
</DataTemplate>
""";
        return (DataTemplate)XamlReader.Load(xaml);
    }

    private UIElement BuildLinkInstall()
    {
        var layout = new StackPanel
        {
            Spacing = 16,
            MaxWidth = 820,
            Padding = new Thickness(12, 24, 12, 12),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        layout.Children.Add(new TextBlock
        {
            Text = "Install ZIP files",
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        layout.Children.Add(new TextBlock
        {
            Text = "Select one or more ZIP files. The manager detects Mod.io cooked plugin archives and UE4SS mods automatically, including mixed selections.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });

        _ue4ssZipWarning = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange),
            Visibility = Visibility.Collapsed
        };
        layout.Children.Add(_ue4ssZipWarning);
        UpdateZipInstallState();

        _chooseZipButton = new Button
        {
            Content = "Choose ZIP files",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            IsEnabled = false
        };
        _chooseZipButton.Click += async (_, _) => await ChooseZipAsync();
        layout.Children.Add(_chooseZipButton);
        UpdateZipInstallState();
        layout.Children.Add(_zipStatus);

        return new ScrollViewer { Content = layout, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void UpdateZipInstallState()
    {
        if (_ue4ssZipWarning == null || _chooseZipButton == null) return;

        var hasGameFolder = _service.HasValidGamePath;
        _chooseZipButton.IsEnabled = hasGameFolder;

        if (!hasGameFolder)
        {
            _ue4ssZipWarning.Text = "Select a valid game folder in Settings.";
            _ue4ssZipWarning.Visibility = Visibility.Visible;
        }
        else
        {
            _ue4ssZipWarning.Text = "";
            _ue4ssZipWarning.Visibility = Visibility.Collapsed;
        }
    }
    private int TotalPages => Math.Max(1, (int)Math.Ceiling(_total / 24.0));

    private void UpdatePaginationButtons()
    {
        bool canNavigate = !_busy && _service.IsApiKeyValid;
        _previous.IsEnabled = canNavigate && _page > 1;
        _next.IsEnabled = canNavigate && _page < TotalPages;
    }

    private async Task NavigateToPageAsync(int targetPage)
    {
        if (_busy || targetPage < 1 || targetPage > TotalPages) return;

        int previousPage = _page;
        _page = targetPage;
        if (!await SearchAsync())
        {
            _page = previousPage;
            _pageLabel.Text = "Page " + _page + " of " + TotalPages;
            UpdatePaginationButtons();
        }
    }

    private async Task SearchFromInputAsync()
    {
        // Deliberately leave Search enabled; clicking it with no text has no effect.
        if (string.IsNullOrWhiteSpace(_search.Text))
            return;

        _page = 1;
        await SearchAsync(forceRefresh: true);
    }
    private async Task<bool> SearchAsync(bool forceRefresh = false)
    {
        if (_busy) return false;
        if (!_service.IsApiKeyValid)
        {
            _resultLabel.Text = "The API key is not correct or valid. Please check it in Settings and try again.";
            _previous.IsEnabled = false;
            _next.IsEnabled = false;
            return false;
        }

        _busy = true;
        _previous.IsEnabled = false;
        _next.IsEnabled = false;
        _resultLabel.Text = "Searching mods...";
        try
        {
            var sort = _sort.SelectedIndex switch { 1 => "Downloads", 2 => "Name", _ => "Updated" };
            var result = await _service.SearchModsCachedAsync(_search.Text, sort, _page, forceRefresh);

            _mods.Clear();
            foreach (var mod in result.Mods)
            {
                if (mod.Thumbnail == null && !string.IsNullOrWhiteSpace(mod.ThumbnailUrl))
                {
                    try { mod.Thumbnail = new BitmapImage(new Uri(mod.ThumbnailUrl)) { DecodePixelWidth = 400 }; }
                    catch { }
                }
                _mods.Add(mod);
            }

            UpdateCardWidths(_grid.ActualWidth);
            _total = result.Total;
            _resultLabel.Text = _total.ToString("N0") + " mods";
            _pageLabel.Text = "Page " + _page + " of " + TotalPages;
            return true;
        }
        catch (Exception ex)
        {
            _resultLabel.Text = ex.Message.Contains("API key", StringComparison.OrdinalIgnoreCase)
                ? "The API key is not correct or valid. Please check it in Settings and try again."
                : "Could not load the catalogue. Please check your connection and try again.";
            return false;
        }
        finally
        {
            _busy = false;
            UpdatePaginationButtons();
        }
    }
    private async Task ShowDetailsAsync(BrowseMod mod)
    {
        var content = new StackPanel { Spacing = 12 };
        if (!string.IsNullOrWhiteSpace(mod.ImageUrl))
        {
            try { content.Children.Add(new Image { Source = new BitmapImage(new Uri(mod.ImageUrl)), Width = 560, Height = 260, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform }); }
            catch { }
        }
        content.Children.Add(new TextBlock { Text = mod.Summary, FontSize = 15, TextWrapping = TextWrapping.Wrap, Opacity = 0.82 });
        content.Children.Add(new TextBlock
        {
            Text = ModManagerService.CleanHtml(mod.Description),
            TextWrapping = TextWrapping.Wrap
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = mod.Name,
            Content = new ScrollViewer { Content = content, MaxHeight = 540 },
            PrimaryButtonText = "Download & Install",
            SecondaryButtonText = "Open Mod.io",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            ModManagerService.OpenUrl(mod.Url);
        }
        else if (result == ContentDialogResult.Primary)
        {
            try
            {
                await _service.InstallFromModIoAsync(mod);
                await ShowMessageAsync("Mod installed", mod.Name + " is installed and enabled for the next game launch.");
            }
            catch (Exception ex) { await ShowMessageAsync("Install failed", ex.Message); }
        }
    }

    private async Task ChooseZipAsync()
    {
        if (!_service.HasValidGamePath)
            return;
        if (ModManagerService.IsGameRunning())
        {
            _zipStatus.Text = "Close Abiotic Factor before installing mods.";
            return;
        }

        Directory.CreateDirectory(_service.ManagerDownloadsFolder);
        var files = NativeFilePicker.PickMultipleZipFiles(
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!),
            _service.ManagerDownloadsFolder);
        if (files.Length == 0) return;

        var installed = new List<string>();
        var failures = new List<string>();
        var skipped = new List<string>();
        var ue4ssNotInstalled = new List<string>();
        _zipStatus.Text = "Inspecting and installing " + files.Length + " ZIP file(s)...";

        foreach (var filePath in files)
        {
            var fileName = Path.GetFileName(filePath);
            try
            {
                var info = _service.InspectLocalZip(filePath);
                if (string.Equals(info.Type, "Mod.io", StringComparison.OrdinalIgnoreCase))
                {
                    var id = info.Id;
                    if (id <= 0)
                    {
                        var prompt = new StackPanel { Spacing = 8 };
                        prompt.Children.Add(new TextBlock
                        {
                            Text = "The Mod.io ID wasn't found in this archive's filename or .uplugin metadata. Enter the mod's numeric ID to continue.",
                            TextWrapping = TextWrapping.Wrap
                        });
                        var input = new TextBox { PlaceholderText = "Numeric Mod.io ID" };
                        prompt.Children.Add(input);
                        var dialog = new ContentDialog
                        {
                            XamlRoot = XamlRoot,
                            Title = "Mod.io ID required",
                            Content = prompt,
                            PrimaryButtonText = "Install",
                            CloseButtonText = "Skip",
                            DefaultButton = ContentDialogButton.Primary
                        };
                        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
                            !long.TryParse(input.Text.Trim(), out id) || id <= 0)
                        {
                            skipped.Add(fileName + " (Mod.io ID not provided)");
                            continue;
                        }
                    }

                    var mod = await _service.InstallLocalZipAsync(filePath, id, info.Name);
                    installed.Add(mod.Name + " (Mod.io)");
                }
                else if (string.Equals(info.Type, "UE4SS", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_service.IsUe4ssInstalled)
                    {
                        ue4ssNotInstalled.Add(fileName);
                        continue;
                    }

                    var names = await _service.InstallUe4ssModsFromZipAsync(filePath);
                    installed.AddRange(names.Select(name => name + " (UE4SS)"));
                }
                else
                {
                    failures.Add(fileName + ": unsupported ZIP type.");
                }
            }
            catch (Exception ex)
            {
                failures.Add(fileName + ": " + ex.Message);
            }
        }

        var lines = new List<string>();
        if (installed.Count > 0) lines.Add("Installed: " + string.Join(", ", installed));
        if (skipped.Count > 0) lines.Add("Skipped: " + string.Join("; ", skipped));
        if (ue4ssNotInstalled.Count > 0) lines.Add("UE4SS is not present. The following UE4SS mod(s) could not be installed: " + string.Join(", ", ue4ssNotInstalled) + ". Install UE4SS via the Settings menu.");
        if (failures.Count > 0) lines.Add("Failed: " + string.Join("; ", failures));
        if (lines.Count == 0) lines.Add("No ZIPs were installed.");
        _zipStatus.Text = string.Join(Environment.NewLine, lines);
    }
    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, CloseButtonText = "OK" };
        await dialog.ShowAsync();
    }

    private void UpdateInstalledLabels()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var mod in _mods)
            {
                var installed = _service.Installed.FirstOrDefault(x => x.Id == mod.Id.ToString());
                mod.InstalledText = installed == null ? "" :
                    installed.UpdateAvailable ? "Installed Â· update available" : "Installed";
            }
        });
    }
}
