using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AbioticFactorModManager;

public sealed class InstalledModsPage : Page
{
    private readonly ModManagerService _service;
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.None,
        IsItemClickEnabled = false,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBox _filter = new() { PlaceholderText = "Filter installed mods...", Width = 300 };
    private readonly TextBlock _count = new() { Opacity = 0.72, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Opacity = 0.72, TextWrapping = TextWrapping.Wrap };
    private readonly GridView _waffle = new()
    {
        SelectionMode = ListViewSelectionMode.None,
        IsItemClickEnabled = false,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly Button _listViewButton = new() { Content = "List", IsEnabled = false };
    private readonly Button _waffleViewButton = new() { Content = "Waffle" };
    private readonly Button _manageButton = new() { Content = "Manage" };
    private readonly ComboBox _waffleSortDropdown = new() { MinWidth = 150 };
    private StackPanel? _waffleSortRow;
    private string _waffleSortMode = "Name";
    private readonly TextBlock _selectionCount = new() { Opacity = 0.72, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _selectionModeToggle = new() { Content = "Select", VerticalAlignment = VerticalAlignment.Center };
    private readonly HashSet<string> _selectedModIds = new(StringComparer.OrdinalIgnoreCase);
    private const int VirtualKeyControl = 0x11;
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    private MenuFlyoutItem? _menuCheckUpdates;
    private MenuFlyoutItem? _menuUpdateSelected;
    private MenuFlyoutItem? _menuUpdateAll;
    private MenuFlyoutItem? _menuEnableSelected;
    private MenuFlyoutItem? _menuDisableSelected;
    private MenuFlyoutItem? _menuUninstallSelected;
    private bool _isWaffleView;
    private bool _waffleThumbnailsLoaded;
    private readonly Dictionary<string, BitmapImage> _installedThumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Border Frame, Image Image)> _waffleThumbnailElements = new(StringComparer.OrdinalIgnoreCase);
    private double _waffleCardWidth = 280;
    private double _waffleThumbnailHeight = 135;
    private double _waffleCardHeight = 280;
    private string _sortColumn = "Name";
    private bool _ascending = true;
    private bool _building;
    private bool _initialLoadHandled;
    private Grid? _rootLayout;
    private Grid? _headerGrid;
    private Grid? _contentHost;
    private static double[]? _resizedColumnWidths;
    private int _resizeColumn = -1;
    private double _resizeStartX, _resizeStartLeft, _resizeStartRight;

    public InstalledModsPage(ModManagerService service)
    {
        _service = service;
        Content = BuildPage();
        _service.InstalledChanged += OnInstalledChanged;
        Loaded += (_, _) => { if (_initialLoadHandled) return; _initialLoadHandled = true; RefreshRows(); };
    }

    private UIElement BuildPage()
    {
        var root = new Grid { Padding = new Thickness(28, 20, 28, 24), RowSpacing = 14 };
        _rootLayout = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var title = new StackPanel { Spacing = 3 };
        title.Children.Add(new TextBlock { Text = "Installed Mods", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        title.Children.Add(new TextBlock { Text = "Manage your installed mods, check updates, and select multiple mods with Ctrl+Click.", Opacity = 0.68 });
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var toolbar = new Grid { ColumnSpacing = 12 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var filter = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        _filter.TextChanged += (_, _) => RefreshRows();
        filter.Children.Add(_filter);
        filter.Children.Add(_selectionModeToggle);
        Grid.SetColumn(filter, 0);
        toolbar.Children.Add(filter);

        var rightControls = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var rightButtonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _listViewButton.Click += async (_, _) => await SetWaffleViewAsync(false);
        _waffleViewButton.Click += async (_, _) => await SetWaffleViewAsync(true);
        rightButtonRow.Children.Add(_count);
        rightButtonRow.Children.Add(_selectionCount);
        rightButtonRow.Children.Add(_listViewButton);
        rightButtonRow.Children.Add(_waffleViewButton);
        _selectionModeToggle.Checked += (_, _) =>
        {
            _status.Text = "Selection mode is on. Click rows or cards to add or remove them from the selection.";
            UpdateSelectionState();
        };
        _selectionModeToggle.Unchecked += (_, _) =>
        {
            _selectedModIds.Clear();
            RefreshSelectionVisuals();
            _status.Text = "Selection mode is off. Hold Ctrl while clicking to select multiple mods.";
            UpdateSelectionState();
        };


        var manageMenu = new MenuFlyout();
        _menuCheckUpdates = new MenuFlyoutItem { Text = "Check for updates" };
        _menuCheckUpdates.Click += async (_, _) => await CheckUpdatesAsync();
        _menuUpdateSelected = new MenuFlyoutItem { Text = "Update selected" };
        _menuUpdateSelected.Click += async (_, _) => await UpdateSelectedAsync();
        _menuUpdateAll = new MenuFlyoutItem { Text = "Update all available" };
        _menuUpdateAll.Click += async (_, _) => await UpdateAllAvailableAsync();
        _menuEnableSelected = new MenuFlyoutItem { Text = "Enable selected" };
        _menuEnableSelected.Click += async (_, _) => await SetSelectedEnabledAsync(true);
        _menuDisableSelected = new MenuFlyoutItem { Text = "Disable selected" };
        _menuDisableSelected.Click += async (_, _) => await SetSelectedEnabledAsync(false);
        _menuUninstallSelected = new MenuFlyoutItem { Text = "Uninstall selected" };
        _menuUninstallSelected.Click += async (_, _) => await UninstallSelectedAsync();

        manageMenu.Items.Add(_menuCheckUpdates);
        manageMenu.Items.Add(_menuUpdateSelected);
        manageMenu.Items.Add(_menuUpdateAll);
        manageMenu.Items.Add(new MenuFlyoutSeparator());
        manageMenu.Items.Add(_menuEnableSelected);
        manageMenu.Items.Add(_menuDisableSelected);
        manageMenu.Items.Add(_menuUninstallSelected);
        manageMenu.Items.Add(new MenuFlyoutSeparator());

        var refresh = new MenuFlyoutItem { Text = "Refresh installed list" };
        refresh.Click += (_, _) => { _service.RefreshInstalled(true); RefreshRows(); };
        var folder = new MenuFlyoutItem { Text = "Open Mods folder" };
        folder.Click += (_, _) => _service.OpenModsFolder();
        manageMenu.Items.Add(refresh);
        manageMenu.Items.Add(folder);
        _manageButton.Click += (_, _) => manageMenu.ShowAt(_manageButton);
        rightButtonRow.Children.Add(_manageButton);
        rightControls.Children.Add(rightButtonRow);

        _waffleSortDropdown.Items.Add("Name");
        _waffleSortDropdown.Items.Add("Enabled / Disabled");
        _waffleSortDropdown.Items.Add("Loader");
        _waffleSortDropdown.SelectedIndex = 0;
        _waffleSortDropdown.SelectedItem = "Name";
        _waffleSortMode = "Name";
        _waffleSortDropdown.Width = 170;
        _waffleSortDropdown.SelectionChanged += (_, _) =>
        {
            _waffleSortMode = _waffleSortDropdown.SelectedItem?.ToString() ?? "Name";
            if (_isWaffleView) RefreshRows();
        };
        _waffleSortRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        _waffleSortRow.Children.Add(new TextBlock { Text = "Sort by", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.72 });
        _waffleSortRow.Children.Add(_waffleSortDropdown);
        rightControls.Children.Add(_waffleSortRow);

        Grid.SetColumn(rightControls, 1);
        toolbar.Children.Add(rightControls);
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);

        _headerGrid = BuildColumnHeader();
        Grid.SetRow(_headerGrid, 2);
        root.Children.Add(_headerGrid);

        _list.SelectionChanged += (_, _) => UpdateSelectionState();
        _waffle.SelectionChanged += (_, _) => UpdateSelectionState();
        _list.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _list.BorderThickness = new Thickness(0);
        _waffle.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _waffle.BorderThickness = new Thickness(0);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_waffle, ScrollBarVisibility.Auto);
        _waffle.SizeChanged += (_, e) =>
        {
            UpdateWaffleCardLayout(e.NewSize.Width);
            DispatcherQueue.TryEnqueue(() => UpdateWaffleCardLayout(_waffle.ActualWidth));
        };
        _waffle.Loaded += (_, _) =>
        {
            UpdateWaffleCardLayout(_waffle.ActualWidth);
            DispatcherQueue.TryEnqueue(() => UpdateWaffleCardLayout(_waffle.ActualWidth));
        };
        _waffle.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("""
<ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <ItemsWrapGrid Orientation="Horizontal" MaximumRowsOrColumns="6" ItemWidth="236" ItemHeight="310"/>
</ItemsPanelTemplate>
""");
        _list.Visibility = Visibility.Visible;
        _waffle.Visibility = Visibility.Collapsed;

        _contentHost = new Grid();
        _contentHost.Children.Add(_list);
        _contentHost.Children.Add(_waffle);
        Grid.SetRow(_contentHost, 3);
        root.Children.Add(_contentHost);
        UpdateSelectionState();
        return root;
    }

    private Grid BuildColumnHeader()
    {
        var grid = CreateColumns();
        grid.Margin = new Thickness(8, 0, 8, 0);
        grid.Padding = new Thickness(10, 8, 10, 8);
        grid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AddSortHeader(grid, "Enabled", "Enabled", 0);
        AddSortHeader(grid, "Name", "Name", 1);
        AddSortHeader(grid, "Version", "Version", 2);
        AddSortHeader(grid, "Loader", "Loader", 3);
        AddSortHeader(grid, "ID / Folder", "ID", 4);
        var actionsLabel = new TextBlock { Text = "Actions", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.72, Margin = new Thickness(4, 6, 4, 6) };
        Grid.SetColumn(actionsLabel, 5);
        grid.Children.Add(actionsLabel);
        for (int i = 0; i < 5; i++) AddResizeGrip(grid, i);
        return grid;
    }

    private static Grid CreateColumns()
    {
        var grid = new Grid { ColumnSpacing = 10 };
        if (_resizedColumnWidths is { Length: 6 } widths)
        {
            foreach (var width in widths)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width), MinWidth = 48 });
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.6, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.85, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        }
        return grid;
    }
    private void AddResizeGrip(Grid grid, int leftColumn)
    {
        var grip = new Border { Width = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), IsHitTestVisible = true };
        grip.PointerPressed += (_, e) =>
        {
            _resizeColumn = leftColumn;
            _resizeStartX = e.GetCurrentPoint(grid).Position.X;
            _resizeStartLeft = grid.ColumnDefinitions[leftColumn].ActualWidth;
            _resizeStartRight = grid.ColumnDefinitions[leftColumn + 1].ActualWidth;
            grip.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (_resizeColumn != leftColumn || !e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
            var delta = e.GetCurrentPoint(grid).Position.X - _resizeStartX;
            var left = Math.Max(64, _resizeStartLeft + delta);
            var right = Math.Max(64, _resizeStartRight - delta);
            grid.ColumnDefinitions[leftColumn].Width = new GridLength(left);
            grid.ColumnDefinitions[leftColumn + 1].Width = new GridLength(right);
            foreach (var item in _list.Items)
                if (item is Border border && border.Child is Grid row && row.ColumnDefinitions.Count == 6)
                {
                    row.ColumnDefinitions[leftColumn].Width = new GridLength(left);
                    row.ColumnDefinitions[leftColumn + 1].Width = new GridLength(right);
                }
        };
        grip.PointerReleased += (_, e) =>
        {
            if (_resizeColumn != leftColumn) return;
            _resizeColumn = -1;
            _resizedColumnWidths = grid.ColumnDefinitions.Select(c => Math.Max(48, c.ActualWidth)).ToArray();
            grip.ReleasePointerCapture(e.Pointer);
            RefreshRows();
            e.Handled = true;
        };
        Grid.SetColumn(grip, leftColumn);
        grid.Children.Add(grip);
    }
    private void AddSortHeader(Grid grid, string label, string key, int column)
    {
        var text = key == _sortColumn ? label + (_ascending ? "  \u2191" : "  \u2193") : label;
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 6, 4, 6)
        };
        button.Click += (_, _) =>
        {
            if (_sortColumn == key) _ascending = !_ascending;
            else { _sortColumn = key; _ascending = true; }
            RefreshHeader();
            RefreshRows();
        };
        Grid.SetColumn(button, column);
        grid.Children.Add(button);
    }

    private void RefreshHeader()
    {
        if (_rootLayout == null || _headerGrid == null) return;
        var index = _rootLayout.Children.IndexOf(_headerGrid);
        if (index < 0) return;
        _rootLayout.Children.Remove(_headerGrid);
        _headerGrid = BuildColumnHeader();
        _headerGrid.Visibility = _isWaffleView ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(_headerGrid, 2);
        _rootLayout.Children.Insert(index, _headerGrid);
    }

    private List<InstalledMod> GetSelectedMods()
    {
        return _service.Installed
            .Where(mod => _selectedModIds.Contains(mod.Id))
            .ToList();
    }

    private void RefreshRows(HashSet<string>? preserveSelection = null)
    {
        if (preserveSelection != null)
        {
            _selectedModIds.Clear();
            _selectedModIds.UnionWith(preserveSelection);
        }
        var installedIds = _service.Installed.Select(mod => mod.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedModIds.RemoveWhere(id => !installedIds.Contains(id));

        _building = true;
        try
        {
            _list.Items.Clear();
            _waffle.Items.Clear();

            var query = _filter.Text.Trim();
            IEnumerable<InstalledMod> rows = _service.Installed;
            if (!string.IsNullOrWhiteSpace(query))
                rows = rows.Where(mod => mod.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    mod.IdDisplay.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    mod.LoaderDisplay.Contains(query, StringComparison.OrdinalIgnoreCase));

            var materialized = ApplySort(rows).ToList();
            foreach (var mod in materialized)
            {
                var item = _isWaffleView ? BuildWaffleCard(mod) : BuildRow(mod);
                if (_isWaffleView) _waffle.Items.Add(item);
                else _list.Items.Add(item);
            }

            var total = _service.Installed.Count;
            var enabled = _service.Installed.Count(mod => mod.Enabled);
            var updates = _service.CountUpdates();
            _count.Text = $"{total} installed | {enabled} enabled | {updates} update" + (updates == 1 ? "" : "s") + " available";
            _status.Text = updates > 0
                ? updates + " update(s) available. Use Ctrl+Click or turn on Select mode, then choose an action from Manage."
                : "Use Ctrl+Click or turn on Select mode to select multiple mods for actions in Manage.";
        }
        finally { _building = false; }

        if (_headerGrid != null)
            _headerGrid.Visibility = _isWaffleView ? Visibility.Collapsed : Visibility.Visible;
        _list.Visibility = _isWaffleView ? Visibility.Collapsed : Visibility.Visible;
        _waffle.Visibility = _isWaffleView ? Visibility.Visible : Visibility.Collapsed;
        RefreshSelectionVisuals();
        UpdateSelectionState();
    }

    private static bool IsControlKeyDown() => (GetAsyncKeyState(VirtualKeyControl) & 0x8000) != 0;

    private static bool IsInteractiveSource(DependencyObject? source, DependencyObject surface)
    {
        var current = source;
        while (current != null && current != surface)
        {
            if (current is Button || current is CheckBox)
                return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void AttachModSurfaceInteraction(Border surface, InstalledMod mod, bool waffle)
    {
        surface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(async (_, e) =>
        {
            if (!e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed)
                return;
            if (IsInteractiveSource(e.OriginalSource as DependencyObject, surface))
                return;

            bool select = _selectionModeToggle.IsChecked == true || IsControlKeyDown();
            if (select)
            {
                if (!_selectedModIds.Add(mod.Id))
                    _selectedModIds.Remove(mod.Id);
                RefreshSelectionVisuals();
                UpdateSelectionState();
                e.Handled = true;
                return;
            }

            if (waffle)
            {
                e.Handled = true;
                await ShowInstalledModDetailsAsync(mod);
            }
            // A plain click in List view intentionally does not select.
        }), true);
    }

    private void ApplySelectionVisual(Border surface, InstalledMod mod)
    {
        bool selected = _selectedModIds.Contains(mod.Id);
        surface.BorderBrush = new SolidColorBrush(selected ? Microsoft.UI.Colors.DodgerBlue : Microsoft.UI.Colors.Transparent);
        surface.Background = selected
            ? new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue) { Opacity = 0.12 }
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void RefreshSelectionVisuals()
    {
        foreach (var surface in _list.Items.OfType<Border>())
            if (surface.Tag is InstalledMod mod) ApplySelectionVisual(surface, mod);
        foreach (var surface in _waffle.Items.OfType<Border>())
            if (surface.Tag is InstalledMod mod) ApplySelectionVisual(surface, mod);
    }
    private IEnumerable<InstalledMod> ApplySort(IEnumerable<InstalledMod> rows)
    {
        if (_isWaffleView)
        {
            return _waffleSortMode switch
            {
                "Enabled / Disabled" => rows
                    .OrderByDescending(mod => mod.Enabled)
                    .ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase),
                "Loader" => rows
                    .OrderBy(mod => mod.LoaderDisplay, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase),
                _ => rows.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            };
        }

        // List view retains its independent column-header sort and direction.
        Func<InstalledMod, object> key = _sortColumn switch
        {
            "Version" => mod => mod.Version ?? "",
            "Loader" => mod => mod.LoaderDisplay,
            "ID" => mod => mod.IdDisplay,
            "Enabled" => mod => mod.Enabled,
            _ => mod => mod.Name
        };
        return _ascending ? rows.OrderBy(key) : rows.OrderByDescending(key);
    }

    private UIElement BuildRow(InstalledMod mod)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(4, 3, 4, 3),
            Tag = mod,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        ApplySelectionVisual(border, mod);
        AttachModSurfaceInteraction(border, mod, false);

        var grid = CreateColumns();
        var toggle = new CheckBox { IsChecked = mod.Enabled, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        toggle.Checked += (_, _) => { if (!_building) ToggleMod(mod, true); };
        toggle.Unchecked += (_, _) => { if (!_building) ToggleMod(mod, false); };
        AddCell(grid, toggle, 0);

        var name = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = mod.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (mod.UpdateAvailable) name.Children.Add(CreateUpdateVersionLine(mod));
        if (!string.IsNullOrWhiteSpace(mod.SourceUrl))
            name.Children.Add(new TextBlock { Text = mod.SourceUrl, FontSize = 10, Opacity = 0.48, TextTrimming = TextTrimming.CharacterEllipsis });
        AddCell(grid, name, 1);

        AddCell(grid, new TextBlock { Text = mod.VersionDisplay, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis }, 2);
        AddCell(grid, new TextBlock { Text = mod.LoaderDisplay, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 }, 3);
        AddCell(grid, new TextBlock { Text = mod.IdDisplay, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis }, 4);
        AddCell(grid, BuildModActions(mod, waffleView: false), 5);

        border.Child = grid;
        return border;
    }

    private TextBlock CreateUpdateVersionLine(InstalledMod mod)
    {
        var current = mod.VersionDisplay;
        var available = string.IsNullOrWhiteSpace(mod.AvailableVersion) ? "New version" : mod.AvailableVersion;
        return new TextBlock
        {
            Text = "Update available: " + current + " -> " + available,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.LightGreen),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
    }

    private StackPanel BuildModActions(InstalledMod mod, bool waffleView)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        var configFiles = _service.GetModConfigFiles(mod);
        if (configFiles.Count > 0)
        {
            var configure = new Button { Content = "Configure", Padding = new Thickness(9, 3, 9, 3), MinWidth = 0 };
            configure.Click += async (_, _) => await ConfigureModAsync(mod);
            actions.Children.Add(configure);
        }

        if (mod.UpdateAvailable && string.Equals(mod.SourceType, "Mod.io", StringComparison.OrdinalIgnoreCase))
        {
            var update = new Button { Content = "Update", Padding = new Thickness(9, 3, 9, 3), MinWidth = 0, IsEnabled = !ModManagerService.IsGameRunning() };
            update.Click += async (_, _) => await UpdateSingleModAsync(mod);
            actions.Children.Add(update);
        }

        if (!waffleView && string.Equals(mod.SourceType, "Mod.io", StringComparison.OrdinalIgnoreCase))
        {
            var details = new Button { Content = "Details", Padding = new Thickness(9, 3, 9, 3), MinWidth = 0 };
            details.Click += async (_, _) => await ShowInstalledModDetailsAsync(mod);
            actions.Children.Add(details);
        }

        // The List view retains its empty-action placeholder. Waffle cards do not.
        if (actions.Children.Count == 0 && !waffleView)
            actions.Children.Add(new TextBlock { Text = "—", Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center });
        return actions;
    }

    private Border BuildWaffleCard(InstalledMod mod)
    {
        double cardWidth = _waffleCardWidth;
        double thumbnailHeight = _waffleThumbnailHeight;
        double thumbnailWidth = Math.Max(160, cardWidth - 20);

        var card = new Border
        {
            Width = cardWidth,
            Height = _waffleCardHeight,
            Margin = new Thickness(5),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Tag = mod
        };
        ApplySelectionVisual(card, mod);
        AttachModSurfaceInteraction(card, mod, true);

        var content = new Grid { RowSpacing = 8 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        bool hasThumbnail = _installedThumbnails.TryGetValue(mod.Id, out var thumbnail);
        var image = new Image
        {
            Source = hasThumbnail ? thumbnail : null,
            Width = thumbnailWidth,
            Height = thumbnailHeight,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = hasThumbnail ? Visibility.Visible : Visibility.Collapsed
        };

        var imageFrame = new Border
        {
            Width = thumbnailWidth,
            Height = thumbnailHeight,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Microsoft.UI.Colors.DarkSlateGray),
            BorderThickness = new Thickness(0),
            Child = hasThumbnail
                ? image
                : new TextBlock
                {
                    Text = mod.LoaderDisplay,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.75
                }
        };
        if (hasThumbnail)
        {
            ApplyRoundedThumbnailClip(image);
            image.Loaded += (_, _) => ApplyRoundedThumbnailClip(image);
            image.SizeChanged += (_, _) => ApplyRoundedThumbnailClip(image);
        }
        _waffleThumbnailElements[mod.Id] = (imageFrame, image);
        Grid.SetRow(imageFrame, 0);
        content.Children.Add(imageFrame);

        var details = new StackPanel { Spacing = 4 };
        details.Children.Add(new TextBlock
        {
            Text = mod.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2
        });
        details.Children.Add(new TextBlock
        {
            Text = mod.LoaderDisplay + " · " + mod.VersionDisplay,
            FontSize = 11,
            Opacity = 0.65
        });
        if (mod.UpdateAvailable)
            details.Children.Add(CreateUpdateVersionLine(mod));
        Grid.SetRow(details, 1);
        content.Children.Add(details);

        var actionRow = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var actions = BuildModActions(mod, waffleView: true);
        actions.Spacing = 5;
        actions.VerticalAlignment = VerticalAlignment.Center;
        actions.HorizontalAlignment = HorizontalAlignment.Left;
        if (actions.Children.Count > 0)
        {
            Grid.SetColumn(actions, 0);
            actionRow.Children.Add(actions);
        }

        var enabledToggle = new CheckBox
        {
            IsChecked = mod.Enabled,
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        enabledToggle.Checked += (_, _) => { if (!_building) ToggleMod(mod, true); };
        enabledToggle.Unchecked += (_, _) => { if (!_building) ToggleMod(mod, false); };

        var enabledLabel = new TextBlock
        {
            Text = mod.Enabled ? "Enabled" : "Disabled",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Opacity = 0.86
        };
        var enabledControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        enabledControls.Children.Add(enabledToggle);
        enabledControls.Children.Add(enabledLabel);
        Grid.SetColumn(enabledControls, 1);
        actionRow.Children.Add(enabledControls);

        Grid.SetRow(actionRow, 3);
        content.Children.Add(actionRow);

        card.Child = content;
        return card;
    }

    private static void ApplyRoundedThumbnailClip(Image image)
    {
        if (image.ActualWidth <= 0 || image.ActualHeight <= 0)
            return;

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(image);
            var compositor = visual.Compositor;
            var geometry = compositor.CreateRoundedRectangleGeometry();
            geometry.Size = new Vector2((float)image.ActualWidth, (float)image.ActualHeight);
            geometry.CornerRadius = new Vector2(10f, 10f);
            visual.Clip = compositor.CreateGeometricClip(geometry);
        }
        catch
        {
            // A thumbnail remains usable even if composition clipping is unavailable.
        }
    }
    private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;

            var nested = FindVisualDescendant<T>(child);
            if (nested != null)
                return nested;
        }
        return null;
    }
    private void UpdateWaffleCardLayout(double availableWidth)
    {
        if (availableWidth <= 0 || double.IsNaN(availableWidth) || double.IsInfinity(availableWidth))
            return;

        const double cardMargin = 12;
        const double verticalScrollbarAllowance = 24;
        const double preferredSlotWidth = 300;
        const double maxCardWidth = 360;
        const double minCardWidth = 280;

        double usableWidth = Math.Max(0, availableWidth - verticalScrollbarAllowance);
        int columns = Math.Max(1, (int)Math.Floor((usableWidth + cardMargin) / preferredSlotWidth));
        columns = Math.Min(columns, 6);

        while (columns < 6 &&
               usableWidth / columns - cardMargin > maxCardWidth &&
               usableWidth / (columns + 1) - cardMargin >= minCardWidth)
        {
            columns++;
        }

        double slotWidth = usableWidth / columns;
        _waffleCardWidth = Math.Max(minCardWidth, slotWidth - cardMargin);
        double thumbnailWidth = Math.Max(160, _waffleCardWidth - 20);
        _waffleThumbnailHeight = thumbnailWidth * (9.0 / 16.0);
        _waffleCardHeight = _waffleThumbnailHeight + 174;

        var panel = _waffle.ItemsPanelRoot as ItemsWrapGrid ?? FindVisualDescendant<ItemsWrapGrid>(_waffle);
if (panel != null)
        {
            panel.ItemWidth = slotWidth;
            panel.ItemHeight = _waffleCardHeight + cardMargin;
        }

        foreach (var card in _waffle.Items.OfType<Border>())
        {
            card.Width = _waffleCardWidth;
            card.Height = _waffleCardHeight;
            if (card.Tag is InstalledMod mod && _waffleThumbnailElements.TryGetValue(mod.Id, out var thumbnail))
            {
                var width = Math.Max(160, _waffleCardWidth - 20);
                thumbnail.Frame.Width = width;
                thumbnail.Frame.Height = _waffleThumbnailHeight;
                thumbnail.Image.Width = width;
                thumbnail.Image.Height = _waffleThumbnailHeight;
            }
        }
    }
    private async Task ShowInstalledModDetailsAsync(InstalledMod installed)
    {
        BrowseMod? details = null;
        bool isModIo = string.Equals(installed.SourceType, "Mod.io", StringComparison.OrdinalIgnoreCase);

        if (isModIo && long.TryParse(installed.Id, out var modId))
        {
            try
            {
                details = await _service.GetModDetailsAsync(modId);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("Couldn't load mod details", ex.Message);
                return;
            }
        }

        var content = new StackPanel { Spacing = 12 };
        if (details != null)
        {
            if (!string.IsNullOrWhiteSpace(details.ImageUrl))
            {
                try
                {
                    content.Children.Add(new Image
                    {
                        Source = new BitmapImage(new Uri(details.ImageUrl)),
                        Width = 560,
                        Height = 260,
                        Stretch = Stretch.Uniform
                    });
                }
                catch { }
            }

            if (!string.IsNullOrWhiteSpace(details.Summary))
                content.Children.Add(new TextBlock { Text = details.Summary, FontSize = 15, TextWrapping = TextWrapping.Wrap, Opacity = 0.82 });

            var description = ModManagerService.CleanHtml(details.Description);
            if (!string.IsNullOrWhiteSpace(description))
                content.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });

            if (!string.IsNullOrWhiteSpace(details.CreatorText))
                content.Children.Add(new TextBlock { Text = details.CreatorText, FontSize = 12, Opacity = 0.7 });
            if (string.IsNullOrWhiteSpace(details.Summary) && string.IsNullOrWhiteSpace(description))
                content.Children.Add(new TextBlock { Text = "This mod has not provided a description.", Opacity = 0.7 });
        }
        else
        {
            content.Children.Add(new TextBlock
            {
                Text = installed.LoaderDisplay + " mod",
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            content.Children.Add(new TextBlock { Text = "Version " + installed.VersionDisplay });
            content.Children.Add(new TextBlock { Text = installed.Enabled ? "Currently enabled" : "Currently disabled" });
            content.Children.Add(new TextBlock
            {
                Text = "This installed mod format does not provide a hosted Mod.io description.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = installed.Name,
            Content = new ScrollViewer { Content = content, MaxHeight = 540, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            SecondaryButtonText = details != null && !string.IsNullOrWhiteSpace(details.Url) ? "Open Mod.io" : null,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary && details != null)
            ModManagerService.OpenUrl(details.Url);
    }
    private async Task SetWaffleViewAsync(bool enabled)
    {
        if (_isWaffleView == enabled) return;
        var selectedIds = GetSelectedMods().Select(mod => mod.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _isWaffleView = enabled;
        _listViewButton.IsEnabled = enabled;
        _waffleViewButton.IsEnabled = !enabled;
        if (_waffleSortRow != null) _waffleSortRow.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        RefreshRows(selectedIds);

        if (!enabled || _waffleThumbnailsLoaded) return;

        _status.Text = "Loading installed mod thumbnails...";
        try
        {
            var urls = await _service.GetInstalledModThumbnailUrlsAsync(_service.Installed);
            foreach (var pair in urls)
            {
                if (string.IsNullOrWhiteSpace(pair.Value)) continue;
                try { _installedThumbnails[pair.Key] = new BitmapImage(new Uri(pair.Value)) { DecodePixelWidth = 360 }; }
                catch { }
            }
            _waffleThumbnailsLoaded = true;
        }
        catch (Exception ex)
        {
            _status.Text = "Some thumbnails could not be loaded: " + ex.Message;
            _waffleThumbnailsLoaded = false;
        }
        RefreshRows(selectedIds);
    }

    private void UpdateSelectionState()
    {
        if (_building) return;
        var selected = GetSelectedMods();
        var selectedUpdates = selected.Where(mod => mod.UpdateAvailable &&
            string.Equals(mod.SourceType, "Mod.io", StringComparison.OrdinalIgnoreCase)).ToList();
        var allUpdates = _service.Installed.Count(mod => mod.UpdateAvailable &&
            string.Equals(mod.SourceType, "Mod.io", StringComparison.OrdinalIgnoreCase));
        var gameRunning = ModManagerService.IsGameRunning();

        _selectionCount.Text = (_selectionModeToggle.IsChecked == true ? "Selection mode" : "") + (selected.Count > 0 ? (_selectionModeToggle.IsChecked == true ? " | " : "") + selected.Count + " selected" : "");
        if (_menuUpdateSelected != null)
        {
            _menuUpdateSelected.Text = "Update selected (" + selectedUpdates.Count + ")";
            _menuUpdateSelected.IsEnabled = selectedUpdates.Count > 0 && !gameRunning;
        }
        if (_menuUpdateAll != null)
        {
            _menuUpdateAll.Text = "Update all available (" + allUpdates + ")";
            _menuUpdateAll.IsEnabled = allUpdates > 0 && !gameRunning;
        }
        if (_menuEnableSelected != null) _menuEnableSelected.IsEnabled = selected.Any(mod => !mod.Enabled) && !gameRunning;
        if (_menuDisableSelected != null) _menuDisableSelected.IsEnabled = selected.Any(mod => mod.Enabled) && !gameRunning;
        if (_menuUninstallSelected != null) _menuUninstallSelected.IsEnabled = selected.Count > 0 && !gameRunning;
    }
    private static void AddCell(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

private async Task ConfigureModAsync(InstalledMod mod)
    {
        var files = _service.GetModConfigFiles(mod).ToList();
        if (files.Count == 0)
        {
            _status.Text = "No supported configuration files were found for " + mod.Name + ".";
            return;
        }

        var drafts = files.ToDictionary(x => x.FullPath, x => x.Content, StringComparer.OrdinalIgnoreCase);
        var selected = files[0];

        var picker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 680,
            SelectedIndex = 0
        };
        foreach (var file in files) picker.Items.Add(file.Name);
        picker.SelectedIndex = 0;

        var editor = new TextBox
        {
            AcceptsReturn = true,
            Text = selected.Content,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 13,
            Height = 420,
            MinWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsSpellCheckEnabled = false
        };
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);

        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex < 0 || picker.SelectedIndex >= files.Count) return;
            drafts[selected.FullPath] = editor.Text;
            selected = files[picker.SelectedIndex];
            editor.Text = drafts[selected.FullPath];
        };

        var layout = new StackPanel { Spacing = 8, MinWidth = 720 };
        layout.Children.Add(new TextBlock
        {
            Text = "Configuration files for " + mod.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        layout.Children.Add(picker);
        layout.Children.Add(new TextBlock
        {
            Text = "Edits are backed up automatically. Most changes require a game restart.",
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(editor);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Configure " + mod.Name,
            Content = layout,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        drafts[selected.FullPath] = editor.Text;

        try
        {
            var changed = 0;
            foreach (var file in files)
            {
                var content = drafts[file.FullPath];
                if (string.Equals(content, file.Content, StringComparison.Ordinal)) continue;
                _service.SaveModConfig(mod, file.FullPath, content);
                changed++;
            }
            _status.Text = changed == 0
                ? "No configuration changes to save."
                : "Saved " + changed + " configuration file(s). Previous versions were backed up; restart the game to apply changes.";
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Couldn't save configuration", ex.Message);
        }
    }
    private async void ToggleMod(InstalledMod mod, bool enabled)
    {
        try
        {
            _service.SetEnabled(mod, enabled);
            RefreshRows();
        }
        catch (Exception ex) { await ShowMessageAsync("Couldn't change mod state", ex.Message); RefreshRows(); }
    }

    private async Task CheckUpdatesAsync()
    {
        try
        {
            _menuCheckUpdates!.IsEnabled = false;
            _menuCheckUpdates!.Text = "Checking...";
            var count = await _service.CheckForUpdatesAsync();
            _status.Text = count == 0 ? "All checked Mod.io mods are up to date." : count + " update(s) available. Use Ctrl+Click to select mods, then choose Manage > Update selected or Manage > Update all.";
        }
        catch (Exception ex) { await ShowMessageAsync("Update check failed", ex.Message); }
        finally { _menuCheckUpdates!.IsEnabled = true; _menuCheckUpdates.Text = "Check for updates"; RefreshRows(); }
    }

    private async Task UpdateSelectedAsync()
    {
        var selected = GetSelectedMods()
            .Where(mod => mod.UpdateAvailable &&
                string.Equals(mod.SourceType, "Mod.io", StringComparison.OrdinalIgnoreCase))
            .ToList();
        await UpdateModsWithConfirmationAsync(selected, "Update selected");
    }

    private async Task UpdateAllAvailableAsync()
    {
        var available = _service.Installed
            .Where(x => x.UpdateAvailable &&
                string.Equals(x.SourceType, "Mod.io", StringComparison.OrdinalIgnoreCase))
            .ToList();
        await UpdateModsWithConfirmationAsync(available, "Update all");
    }

    private async Task UpdateSingleModAsync(InstalledMod mod)
    {
        await UpdateModsWithConfirmationAsync(new List<InstalledMod> { mod }, "Update " + mod.Name);
    }

    private async Task UpdateModsWithConfirmationAsync(List<InstalledMod> targets, string actionTitle)
    {
        var available = targets
            .Where(x => x.UpdateAvailable &&
                string.Equals(x.SourceType, "Mod.io", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();

        if (available.Count == 0)
        {
            _status.Text = actionTitle + ": no selected mods have a pending update.";
            return;
        }
        if (ModManagerService.IsGameRunning())
        {
            await ShowMessageAsync("Close Abiotic Factor first", "Mod files cannot be replaced while the game is running.");
            return;
        }

        var names = string.Join(Environment.NewLine, available.Select(x =>
            "- " + x.Name + (string.IsNullOrWhiteSpace(x.AvailableVersion) ? "" : " (new version " + x.AvailableVersion + ")")));
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = actionTitle + " (" + available.Count + " mod(s))?",
            Content = "Only the listed Mod.io mod folders will be replaced. Existing versions will be backed up." +
                Environment.NewLine + Environment.NewLine + names,
            PrimaryButtonText = "Update",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            _manageButton.IsEnabled = false;
            _manageButton.IsEnabled = false;
            var count = await _service.UpdateModsAsync(available);
            _status.Text = "Updated " + count + " of " + available.Count +
                " selected mod(s). Previous versions were backed up.";
        }
        catch (Exception ex) { await ShowMessageAsync("Update failed", ex.Message); }
        finally { _manageButton.IsEnabled = true; RefreshRows(); }
    }

    private async Task SetSelectedEnabledAsync(bool enabled)
    {
        var targets = GetSelectedMods().Where(mod => mod.Enabled != enabled).ToList();
        if (targets.Count == 0) return;
        if (ModManagerService.IsGameRunning())
        {
            await ShowMessageAsync("Close Abiotic Factor first", "Mod enablement cannot be changed while the game is running.");
            return;
        }

        var action = enabled ? "Enable" : "Disable";
        var names = string.Join(Environment.NewLine, targets.Select(mod => "- " + mod.Name));
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = action + " " + targets.Count + " selected mod(s)?",
            Content = names,
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            foreach (var mod in targets) _service.SetEnabled(mod, enabled);
            _status.Text = action + "d " + targets.Count + " selected mod(s). Changes apply on the next game launch.";
            RefreshRows();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(action + " selected mods failed", ex.Message);
            RefreshRows();
        }
    }
    private async Task UninstallSelectedAsync()
    {
        var selected = GetSelectedMods();
        if (selected.Count == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Uninstall " + selected.Count + " mod(s)?",
            Content = "The selected mod folders will be moved into the manager's Backups folder. This does not change your saves.",
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            _service.UninstallMods(selected);
            RefreshRows();
        }
        catch (Exception ex) { await ShowMessageAsync("Uninstall failed", ex.Message); }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, CloseButtonText = "OK" };
        await dialog.ShowAsync();
    }

    private void OnInstalledChanged()
    {
        DispatcherQueue.TryEnqueue(() => RefreshRows());
    }
}



