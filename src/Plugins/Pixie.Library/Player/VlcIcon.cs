using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pixie.Library.Player;

/// <summary>
/// The official cone for the button — <c>Assets\vlc.ico</c>, the store's <c>vlc</c> asset (VideoLAN's own icon,
/// theirs to trademark; here only to point at VLC). Embedded in the assembly and read as a manifest stream:
/// no pack URI, so nothing depends on how WPF resolves a plugin assembly loaded in its own context.
/// </summary>
internal static class VlcIcon
{
    private const string Resource = "Pixie.Library.Assets.vlc.ico";

    /// <summary>The 32 px frame, frozen — 16 DIPs on screen, still crisp at 200 % DPI. <see langword="null"/> if the resource is missing.</summary>
    public static ImageSource? Load()
    {
        try
        {
            using var stream = typeof(VlcIcon).Assembly.GetManifestResourceStream(Resource);
            if (stream is null)
                return null;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames
                .Where(f => f.PixelWidth >= 32)
                .OrderBy(f => f.PixelWidth).ThenByDescending(f => f.Format.BitsPerPixel)
                .FirstOrDefault() ?? decoder.Frames[0];
            if (frame.CanFreeze)
                frame.Freeze();
            return frame;
        }
        catch (Exception)
        {
            return null;   // a button without its icon still works
        }
    }
}
