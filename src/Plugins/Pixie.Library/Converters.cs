using System.Globalization;
using System.Windows.Data;

namespace Pixie.Library;

/// <summary>"1 arquivo" / "1.441 arquivos" for a folder header's live count.</summary>
public sealed class FileCountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n ? Format.Count(n) + (n == 1 ? " arquivo" : " arquivos") : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
