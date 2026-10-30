using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Pixie.Library;

/// <summary>"1 arquivo" / "1.441 arquivos" for a container's count.</summary>
public sealed class FileCountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n ? Format.Count(n) + (n == 1 ? " arquivo" : " arquivos") : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The two lines of the list: a container (folder, group, "Tudo") and a file (or a member that went missing).</summary>
public sealed class RowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Container { get; set; }
    public DataTemplate? File { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item is LibraryRow { IsContainer: true } ? Container : File;
}
