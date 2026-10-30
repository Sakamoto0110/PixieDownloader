using Pixie.Library.Catalog;

namespace Pixie.Library.Tests;

/// <summary>The path primitives of docs/library-model.md §16 — the one place "same" and "inside" are decided.</summary>
public sealed class PathUtilTests
{
    [Theory]
    [InlineData(@"C:\Music\", @"C:\Music")]
    [InlineData(@"C:\Music\Sets\..\", @"C:\Music")]
    [InlineData(@"C:/Music/Sets", @"C:\Music\Sets")]
    [InlineData(@"\\?\C:\Music\", @"C:\Music")]
    [InlineData(@"\\?\UNC\nas\share\x", @"\\nas\share\x")]
    [InlineData(@"C:\", @"C:\")]
    public void Normalize_gives_the_display_form(string input, string expected)
        => Assert.Equal(expected, PathUtil.Normalize(input));

    [Fact]
    public void Canonical_is_the_lower_cased_identity()
    {
        Assert.Equal(@"c:\music\sets", PathUtil.Canonical(@"C:\MUSIC\Sets\"));
        Assert.True(PathUtil.IsSamePath(@"C:\Music\Sets", @"c:\music\sets\"));
        Assert.True(PathUtil.IsSamePath(@"C:\Music\Sets", @"C:/Music/Sets"));
        Assert.False(PathUtil.IsSamePath(@"C:\Music\Sets", @"C:\Music\Sets2"));
    }

    [Theory]
    [InlineData(@"C:\Music\Sets\a.mp3", @"C:\Music", true)]
    [InlineData(@"C:\Music", @"C:\Music", true)]
    [InlineData(@"C:\Music\", @"C:\Music", true)]
    [InlineData(@"C:\Music2\a.mp3", @"C:\Music", false)]   // the separator decides, never StartsWith
    [InlineData(@"C:\Musi", @"C:\Music", false)]
    [InlineData(@"C:\music\SETS", @"c:\Music", true)]
    [InlineData(@"C:\a.mp3", @"C:\", true)]
    [InlineData(@"D:\Music\a.mp3", @"C:\Music", false)]
    public void IsDescendantOrSame_uses_the_separator(string child, string parent, bool expected)
        => Assert.Equal(expected, PathUtil.IsDescendantOrSame(child, parent));

    [Fact]
    public void IsProperDescendant_excludes_the_folder_itself()
    {
        Assert.True(PathUtil.IsProperDescendant(@"C:\Music\a.mp3", @"C:\Music"));
        Assert.False(PathUtil.IsProperDescendant(@"C:\Music\", @"C:\Music"));
    }

    [Fact]
    public void Relative_is_empty_for_the_folder_itself()
    {
        Assert.Equal(@"Sets\a.mp3", PathUtil.Relative(@"C:\Music\Sets\a.mp3", @"C:\Music\"));
        Assert.Equal("", PathUtil.Relative(@"C:\Music", @"C:\Music\"));
    }

    [Theory]
    [InlineData(null, "caminho vazio")]
    [InlineData("   ", "caminho vazio")]
    [InlineData(@"Music\a.mp3", "caminho relativo")]
    [InlineData(@"\Music\a.mp3", "caminho relativo")]
    [InlineData(@"C:Music", "caminho relativo")]
    [InlineData(@"\\.\PhysicalDrive0", "caminho de dispositivo")]
    [InlineData("C:\\Music\\a\0b.mp3", "caractere inválido")]
    public void TryNormalizeAbsolute_refuses_what_it_cannot_trust(string? input, string reason)
    {
        Assert.False(PathUtil.TryNormalizeAbsolute(input, out var normalized, out var why));
        Assert.Equal("", normalized);
        Assert.Equal(reason, why);
    }

    [Theory]
    [InlineData(@"C:\Music\Sets\", @"C:\Music\Sets")]
    [InlineData(@"  C:\Music\Sets  ", @"C:\Music\Sets")]
    [InlineData(@"\\?\C:\Music", @"C:\Music")]
    [InlineData(@"\\nas\share\Music\", @"\\nas\share\Music")]
    [InlineData(@"C:\", @"C:\")]
    public void TryNormalizeAbsolute_accepts_and_normalizes(string input, string expected)
    {
        Assert.True(PathUtil.TryNormalizeAbsolute(input, out var normalized, out var why));
        Assert.Equal(expected, normalized);
        Assert.Equal("", why);
    }

    [Fact]
    public void VolumeRootOf_names_the_drive_or_the_share()
    {
        Assert.Equal(@"C:\", PathUtil.VolumeRootOf(@"C:\Music\a.mp3"));
        Assert.Equal(@"\\nas\share", PathUtil.VolumeRootOf(@"\\nas\share\Music\a.mp3"));
    }
}
