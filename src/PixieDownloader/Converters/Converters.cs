using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using YtDlpCore;

namespace PixieDownloader.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => value is not Visibility.Visible;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is not true;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is not true;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            _ => true
        };
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class DurationConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not TimeSpan ts)
            return "--:--";
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class StageToTextConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        DownloadStage.Queued => "Na fila",
        DownloadStage.Analyzing => "Analisando",
        DownloadStage.Downloading => "Baixando",
        DownloadStage.Converting => "Convertendo MP3",
        DownloadStage.EmbeddingMetadata => "Aplicando metadados",
        DownloadStage.Done => "Concluído",
        DownloadStage.Failed => "Falhou",
        DownloadStage.Cancelled => "Cancelado",
        _ => ""
    };
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var key = value switch
        {
            LogLevel.Error => "Brush.Error",
            LogLevel.Warning => "Brush.Warning",
            LogLevel.Debug => "Brush.Text.Tertiary",
            _ => "Brush.Text.Secondary"
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class CountToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is int n && n > 0;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Visible when an int count is 0 (e.g. empty-collection placeholders), Collapsed otherwise.</summary>
public sealed class ZeroCountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Resolves a resource-key string (e.g. "Brush.Success") to the corresponding brush.</summary>
public sealed class ResourceKeyToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is string key && Application.Current.TryFindResource(key) is Brush b ? b : Brushes.Gray;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>
/// Loads an image file path into a fully-loaded <see cref="System.Windows.Media.Imaging.BitmapImage"/>
/// (CacheOption=OnLoad so the file handle is released immediately, allowing cache eviction).
/// </summary>
public sealed class PathToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not string path || string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            return null;
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
