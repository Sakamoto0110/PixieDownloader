using PixieDownloader.Sdk;

namespace PluginHost.Tests;

public class SdkVersionTests
{
    [Fact]
    public void Current_is_the_major_minor_of_the_Sdk_assembly()
    {
        var assembly = typeof(SdkVersion).Assembly.GetName().Version!;
        Assert.Equal(new Version(assembly.Major, assembly.Minor), SdkVersion.Current);
    }

    [Theory]
    [InlineData("1.1", true)]      // built against exactly this
    [InlineData("1.1.0", true)]    // the package version spelled out
    [InlineData("1.0", true)]      // an older minor: everything it calls is still here
    [InlineData("1.2", false)]     // built against a newer minor: may call members this host lacks
    [InlineData("2.0", false)]     // another major never mixes
    [InlineData("0.9", false)]
    [InlineData("1", false)]       // not a version Version.TryParse reads — incompatible, not an exception
    [InlineData("v1.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_plugin_runs_on_the_same_major_and_a_minor_no_newer_than_the_host(string? apiVersion, bool expected)
    {
        // Pinned to what the Sdk is today so the table above stays readable; bump it when the Sdk does.
        Assert.Equal(new Version(1, 1), SdkVersion.Current);
        Assert.Equal(expected, SdkVersion.IsCompatible(apiVersion));
    }
}
