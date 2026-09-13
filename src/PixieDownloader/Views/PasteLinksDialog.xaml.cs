using System.Windows;
using System.Windows.Controls;
using PixieDownloader.ViewModels;

namespace PixieDownloader.Views;

/// <summary>"Colar links": a text area for URLs (one per line) plus the list options. Confirm → <see cref="Result"/>.</summary>
public partial class PasteLinksDialog : Window
{
    public PastedLinks? Result { get; private set; }

    public PasteLinksDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => LinksBox.Focus();
    }

    /// <summary>Extracts the http(s) URLs from the text, one per line, in order, without duplicates.</summary>
    internal static IReadOnlyList<string> ParseLinks(string? text)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                seen.Add(line))
                urls.Add(line);
        }
        return urls;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var count = ParseLinks(LinksBox.Text).Count;
        ConfirmButton.IsEnabled = count > 0;
        CountText.Text = count switch
        {
            0 => "Nenhum link ainda",
            1 => "1 link",
            _ => $"{count} links",
        };
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        var urls = ParseLinks(LinksBox.Text);
        if (urls.Count == 0)
            return;
        Result = new PastedLinks(urls, NumberedOption.IsChecked == true);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
