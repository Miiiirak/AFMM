using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AbioticFactorModManager;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
    protected void OnChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class BrowseMod : ObservableModel
{
    private string _name = "";
    private string _summary = "";
    private string _description = "";
    private string _creator = "";
    private string _thumbnailUrl = "";
    private string _imageUrl = "";
    private string _url = "";
    private long _downloads;
    private long _updated;
    private BitmapImage? _thumbnail;
    private string _installedText = "";
    private double _cardWidth = 270;
    private double _thumbnailHeight = 142;

    public long Id { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string Creator { get => _creator; set => Set(ref _creator, value); }
    public string ThumbnailUrl { get => _thumbnailUrl; set => Set(ref _thumbnailUrl, value); }
    public string ImageUrl { get => _imageUrl; set => Set(ref _imageUrl, value); }
    public string Url { get => _url; set => Set(ref _url, value); }
    public long Downloads { get => _downloads; set { if (Set(ref _downloads, value)) OnChanged(nameof(DownloadsText)); } }
    public long Updated { get => _updated; set { if (Set(ref _updated, value)) OnChanged(nameof(UpdatedText)); } }
    public BitmapImage? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }
    public string InstalledText { get => _installedText; set => Set(ref _installedText, value); }
    public double CardWidth { get => _cardWidth; set => Set(ref _cardWidth, value); }
    public double ThumbnailHeight { get => _thumbnailHeight; set => Set(ref _thumbnailHeight, value); }
    public string DownloadsText => Downloads.ToString("N0") + " downloads";
    public string UpdatedText => Updated > 0 ? DateTimeOffset.FromUnixTimeSeconds(Updated).ToLocalTime().ToString("MMM d, yyyy") : "Update date unavailable";
    public string CreatorText => string.IsNullOrWhiteSpace(Creator) ? "Unknown creator" : Creator;
}

public sealed class ModFileRecord
{
    public long Id { get; set; }
    public string FileName { get; set; } = "mod.zip";
    public string Version { get; set; } = "";
    public string BinaryUrl { get; set; } = "";
    public long DateAdded { get; set; }
}

public sealed class InstalledMod : ObservableModel
{
    private string _name = "";
    private string _version = "";
    private bool _enabled = true;
    private string _availableVersion = "";
    private bool _updateAvailable;
    private bool _checking;
    private string _updateStatus = "";
    private string _sourceType = "Mod.io";

    public string Id { get; set; } = "";
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Version { get => _version; set { if (Set(ref _version, value)) OnChanged(nameof(VersionDisplay)); } }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string SourceUrl { get; set; } = "";
    public DateTime InstalledAt { get; set; } = DateTime.Now;
    public string PluginName { get; set; } = "";
    public bool LocalOnly { get; set; }
    public bool InstalledByManager { get; set; }
    public string SourceType { get => _sourceType; set => Set(ref _sourceType, value); }
    public long FileId { get; set; }
    public long FileDateAdded { get; set; }
    public long AvailableFileId { get; set; }
    public string AvailableFileName { get; set; } = "";
    public string AvailableBinaryUrl { get; set; } = "";
    public long AvailableDateAdded { get; set; }
    public DateTime LastUpdateCheck { get; set; }
    public string AvailableVersion { get => _availableVersion; set => Set(ref _availableVersion, value); }
    public bool UpdateAvailable { get => _updateAvailable; set { if (Set(ref _updateAvailable, value)) OnChanged(nameof(UpdateStatus)); } }
    public bool Checking { get => _checking; set { if (Set(ref _checking, value)) OnChanged(nameof(UpdateStatus)); } }
    public string UpdateStatus
    {
        get => Checking ? "Checking…" : UpdateAvailable ? "Update available" :
            string.Equals(SourceType, "FileMod", StringComparison.OrdinalIgnoreCase) ? "File-based mod" :
            !string.Equals(SourceType, "Mod.io", StringComparison.OrdinalIgnoreCase) ? "UE4SS runtime" :
            LocalOnly ? "Local-only mod" : "Up to date";
        set => Set(ref _updateStatus, value);
    }
    public string LoaderDisplay =>
        string.Equals(SourceType, "FileMod", StringComparison.OrdinalIgnoreCase) ? "File patch" :
        string.Equals(SourceType, "UE4SS", StringComparison.OrdinalIgnoreCase) ? "UE4SS" :
        LocalOnly ? "Mod.io · Local" : "Mod.io";
    public string VersionDisplay => string.IsNullOrWhiteSpace(Version) ? "1.0" : Version;
    public string IdDisplay =>
        string.Equals(SourceType, "UE4SS", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SourceType, "FileMod", StringComparison.OrdinalIgnoreCase) ? PluginName : Id;
    public string InstalledAtDisplay => InstalledAt.ToLocalTime().ToString("MMM d, yyyy");
}

public sealed class ModSearchPage
{
    public List<BrowseMod> Mods { get; init; } = new();
    public int Total { get; init; }
}