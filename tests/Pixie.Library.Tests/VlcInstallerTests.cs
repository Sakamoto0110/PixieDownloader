using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Pixie.Library.Player;
using YtDlpCore;

namespace Pixie.Library.Tests;

/// <summary>
/// The VLC download end to end against a fake VideoLAN: a status file, a zip shaped like theirs (built here)
/// and its .sha256, served by a handler that reads files instead of the network. What lands in tools\vlc, what
/// is left out, and what a bad archive does to an install that was already there.
/// </summary>
public sealed class VlcInstallerTests : IDisposable
{
    private readonly TempTree _tree = new("vlc");
    private readonly List<string> _log = [];
    private string ServerDir => _tree.Full("server");
    private string ToolsDir => _tree.Full("tools");

    // ───── Pure helpers ─────

    [Fact]
    public void The_status_text_gives_the_version_on_its_first_line()
    {
        Assert.Equal("3.0.23", VlcInstaller.ParseVersion("3.0.23\nhttp://get.videolan.org/vlc/3.0.23/win64/vlc-3.0.23-win64.exe\nVideoLAN and the VLC development team present VLC 3.0.23 \"Vetinari\".\n"));
        Assert.Equal("4.0.0", VlcInstaller.ParseVersion("\r\n  4.0.0 \r\n"));
        Assert.Null(VlcInstaller.ParseVersion("<html>maintenance</html>"));
        Assert.Null(VlcInstaller.ParseVersion(""));
        Assert.Null(VlcInstaller.ParseVersion("3.0.23-rc1\n"));
    }

    [Fact]
    public void The_sha256_file_is_the_hash_and_the_name()
    {
        Assert.Equal("992d19dbd0b8a7cde9167d2f7780b1ef6f92acc8a71acfa736101a21f35181e1",
            VlcInstaller.ParseSha256("992D19DBD0B8A7CDE9167D2F7780B1EF6F92ACC8A71ACFA736101A21F35181E1 *vlc-3.0.23-win64.zip\n"));
        Assert.Null(VlcInstaller.ParseSha256("not found"));
    }

    [Theory]
    [InlineData("vlc.exe", "pt-BR", true)]
    [InlineData("libvlc.dll", "pt-BR", true)]
    [InlineData("plugins/codec/libavcodec_plugin.dll", "pt-BR", true)]
    [InlineData("lua/http/index.html", "pt-BR", true)]
    [InlineData("axvlc.dll", "pt-BR", false)]
    [InlineData("npvlc.dll", "pt-BR", false)]
    [InlineData("msi/x64/vlc.msm", "pt-BR", false)]
    [InlineData("locale/pt_BR/LC_MESSAGES/vlc.mo", "pt-BR", true)]
    [InlineData("locale/pt/LC_MESSAGES/vlc.mo", "pt-BR", true)]
    [InlineData("locale/pt_PT/LC_MESSAGES/vlc.mo", "pt-BR", false)]
    [InlineData("locale/de/LC_MESSAGES/vlc.mo", "pt-BR", false)]
    [InlineData("locale/de/LC_MESSAGES/vlc.mo", "de-DE", true)]
    [InlineData("locale/pt_BR/LC_MESSAGES/vlc.mo", "en-US", false)]
    public void What_lands_on_disk_is_the_player_and_this_machine_s_language(string path, string culture, bool kept)
        => Assert.Equal(kept, VlcInstaller.Keep(path, new CultureInfo(culture)));

    [Fact]
    public void Locale_names_follow_gettext_and_the_invariant_culture_wants_none()
    {
        Assert.Equal(["pt_BR", "pt"], VlcInstaller.LocaleNames(new CultureInfo("pt-BR")));
        Assert.Equal(["de"], VlcInstaller.LocaleNames(new CultureInfo("de")));
        Assert.Empty(VlcInstaller.LocaleNames(CultureInfo.InvariantCulture));
    }

    // ───── End to end ─────

    [Fact]
    public async Task Installs_the_archive_into_tools_vlc_keeping_only_what_this_machine_uses()
    {
        Serve("9.9.9", BuildArchive("vlc-9.9.9"));
        var installer = NewInstaller();
        var progress = new List<VlcProgress>();

        var version = await installer.InstallAsync(new SyncProgress(progress), CancellationToken.None);

        Assert.Equal("9.9.9", version);
        Assert.True(installer.IsInstalled);
        Assert.Equal(Path.Combine(ToolsDir, "vlc", "vlc.exe"), installer.ExePath);
        Assert.Equal("exe", File.ReadAllText(installer.ExePath));
        Assert.True(File.Exists(Path.Combine(installer.Directory, "plugins", "codec", "libx.dll")));
        Assert.True(File.Exists(Path.Combine(installer.Directory, "locale", "pt_BR", "LC_MESSAGES", "vlc.mo")));
        Assert.False(Directory.Exists(Path.Combine(installer.Directory, "locale", "de")));
        Assert.False(Directory.Exists(Path.Combine(installer.Directory, "msi")));
        Assert.False(File.Exists(Path.Combine(installer.Directory, "axvlc.dll")));
        Assert.Empty(Directory.GetDirectories(ToolsDir, ".~vlc-*"));   // the work folder is gone
        Assert.Empty(Directory.GetFiles(ToolsDir));                    // and so is the zip
        Assert.Contains(progress, p => p.Phase == "baixando" && p.Percent == 100);
        Assert.Contains(progress, p => p.Phase == "extraindo" && p.Percent == 100);
        Assert.Equal("preparando", progress[^1].Phase);
        Assert.Contains(_log, l => l.Contains("baixando o VLC 9.9.9"));
    }

    [Fact]
    public async Task Replaces_an_older_install_whole()
    {
        var old = Path.Combine(ToolsDir, "vlc");
        Directory.CreateDirectory(Path.Combine(old, "plugins"));
        File.WriteAllText(Path.Combine(old, "vlc.exe"), "old");
        File.WriteAllText(Path.Combine(old, "stale.txt"), "from 1.0");
        Serve("9.9.9", BuildArchive("vlc-9.9.9"));
        var installer = NewInstaller();

        await installer.InstallAsync(null, CancellationToken.None);

        Assert.Equal("exe", File.ReadAllText(installer.ExePath));
        Assert.False(File.Exists(Path.Combine(old, "stale.txt")));
        Assert.Empty(Directory.GetDirectories(ToolsDir, ".~vlc-*"));
    }

    [Fact]
    public async Task A_checksum_that_does_not_match_leaves_the_previous_install_alone()
    {
        var old = Path.Combine(ToolsDir, "vlc");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "vlc.exe"), "old");
        Serve("9.9.9", BuildArchive("vlc-9.9.9"), sha256: new string('0', 64));
        var installer = NewInstaller();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(null, CancellationToken.None));

        Assert.Contains("SHA-256", ex.Message);
        Assert.Equal("old", File.ReadAllText(installer.ExePath));
        Assert.Empty(Directory.GetDirectories(ToolsDir, ".~vlc-*"));
    }

    [Fact]
    public async Task An_entry_escaping_the_archive_s_folder_is_refused()
    {
        Serve("9.9.9", BuildArchive("vlc-9.9.9", extra: "vlc-9.9.9/../evil.exe"));
        var installer = NewInstaller();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(null, CancellationToken.None));

        Assert.Contains("fora da pasta", ex.Message);
        Assert.False(File.Exists(Path.Combine(ToolsDir, "evil.exe")));
        Assert.False(installer.IsInstalled);
        Assert.Empty(Directory.GetDirectories(ToolsDir, ".~vlc-*"));
    }

    [Fact]
    public async Task An_archive_that_is_not_shaped_like_VLC_s_is_refused()
    {
        Serve("9.9.9", BuildArchive(""));   // files at the root, no top folder
        var installer = NewInstaller();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(null, CancellationToken.None));

        Assert.Contains("forma do VLC", ex.Message);
        Assert.False(installer.IsInstalled);
    }

    [Fact]
    public async Task A_status_that_is_not_a_version_stops_before_anything_is_downloaded()
    {
        Directory.CreateDirectory(ServerDir);
        File.WriteAllText(Path.Combine(ServerDir, "status-win-x64"), "<html>down</html>");
        var handler = new FileHandler(ServerDir);
        var installer = new VlcInstaller(new HttpClient(handler), ToolsDir, (level, message) => _log.Add(message)) { UiCulture = new CultureInfo("pt-BR") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(null, CancellationToken.None));

        Assert.Equal(["status-win-x64"], handler.Requested);
    }

    // ───── Helpers ─────

    private VlcInstaller NewInstaller()
        => new(new HttpClient(new FileHandler(ServerDir)), ToolsDir, (level, message) => _log.Add(message)) { UiCulture = new CultureInfo("pt-BR") };

    /// <summary>A zip with VLC's shape under <paramref name="top"/>: the exe and its libraries, a plugin, two languages, the extras the install drops.</summary>
    private static byte[] BuildArchive(string top, string? extra = null)
    {
        var prefix = top.Length > 0 ? top + "/" : "";
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }

            Add(prefix + "vlc.exe", "exe");
            Add(prefix + "libvlc.dll", "libvlc");
            Add(prefix + "libvlccore.dll", "core");
            Add(prefix + "axvlc.dll", "activex");
            Add(prefix + "plugins/codec/libx.dll", "plugin");
            Add(prefix + "locale/pt_BR/LC_MESSAGES/vlc.mo", "pt");
            Add(prefix + "locale/de/LC_MESSAGES/vlc.mo", "de");
            Add(prefix + "msi/x64/vlc.msm", "msi");
            if (extra is not null)
                Add(extra, "evil");
        }
        return stream.ToArray();
    }

    private void Serve(string version, byte[] archive, string? sha256 = null)
    {
        Directory.CreateDirectory(ServerDir);
        File.WriteAllText(Path.Combine(ServerDir, "status-win-x64"), $"{version}\nhttp://get.videolan.org/vlc/{version}/win64/vlc-{version}-win64.exe\nVLC {version}\n");
        var name = $"vlc-{version}-win64.zip";
        File.WriteAllBytes(Path.Combine(ServerDir, name), archive);
        File.WriteAllText(Path.Combine(ServerDir, name + ".sha256"), (sha256 ?? Convert.ToHexStringLower(SHA256.HashData(archive))) + " *" + name + "\n");
    }

    /// <summary>Reports inline: the tests run without a synchronization context to post back to.</summary>
    private sealed class SyncProgress(List<VlcProgress> sink) : IProgress<VlcProgress>
    {
        public void Report(VlcProgress value) => sink.Add(value);
    }

    /// <summary>Serves the file named by the request's last path segment, or 404.</summary>
    private sealed class FileHandler(string directory) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = Path.GetFileName(request.RequestUri!.LocalPath);
            Requested.Add(name);
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(path)) };
            response.Content.Headers.ContentLength = new FileInfo(path).Length;
            return Task.FromResult(response);
        }
    }

    public void Dispose() => _tree.Dispose();
}
