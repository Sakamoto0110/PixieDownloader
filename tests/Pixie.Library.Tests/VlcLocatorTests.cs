using System.IO;
using Pixie.Library.Player;

namespace Pixie.Library.Tests;

/// <summary>Where "Usar VLC" looks for a VLC installed in Windows — the order, and the first hit wins.</summary>
public sealed class VlcLocatorTests
{
    [Fact]
    public void The_first_candidate_that_exists_wins_and_none_means_null()
    {
        var candidates = new[] { @"C:\a\vlc.exe", @"C:\b\vlc.exe", @"C:\c\vlc.exe" };

        Assert.Equal(@"C:\b\vlc.exe", VlcLocator.Find(p => p.StartsWith(@"C:\b") || p.StartsWith(@"C:\c"), candidates));
        Assert.Null(VlcLocator.Find(_ => false, candidates));
        Assert.Null(VlcLocator.Find(_ => true, []));
    }

    [Fact]
    public void The_conventional_folders_are_always_among_the_candidates()
    {
        var candidates = VlcLocator.Candidates().ToList();

        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        Assert.False(string.IsNullOrEmpty(programFiles));
        Assert.Contains(Path.Combine(programFiles!, "VideoLAN", "VLC", "vlc.exe"), candidates);
        Assert.All(candidates, c => Assert.EndsWith("vlc.exe", c, StringComparison.OrdinalIgnoreCase));
        // The registry's answer, when there is one, comes before the conventional folders (it says where the installer really put it).
        var conventional = candidates.FindIndex(c => c.EndsWith(@"VideoLAN\VLC\vlc.exe", StringComparison.OrdinalIgnoreCase) && c.StartsWith(programFiles!, StringComparison.OrdinalIgnoreCase));
        Assert.True(conventional >= 0);
    }
}
