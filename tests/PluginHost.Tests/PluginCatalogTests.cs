using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using PixieDownloader.Plugins;
using PixieDownloader.Sdk;
using YtDlpCore;

namespace PluginHost.Tests;

/// <summary>
/// The 1.5 exit criterion, automated: the real Hello plugin (<c>src/Plugins/Pixie.Hello</c>, built before
/// this project) is copied into a <c>plugins/</c> folder under a temp root of its own and driven through
/// <see cref="PluginCatalog"/> exactly as the app does — including the part that matters most, the cast to
/// the host's <see cref="IPixiePlugin"/> across the <see cref="AssemblyLoadContext"/> boundary.
/// </summary>
public sealed class PluginCatalogTests : IDisposable
{
    private const string GreetCapability = "hello.greet";       // what Pixie.Hello's HelloPlugin registers in Configure
    private const string Quiet = "Pixie.Hello.QuietPlugin";     // its other entry type: Configure only, no tab, no capability

    private static readonly string HelloOutput =
        typeof(PluginCatalogTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "HelloPluginOutput").Value!;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pixie-plugin-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    private string PluginsDir => Path.Combine(_root, "plugins");

    // ───── Load ─────

    [Fact]
    public void Hello_loads_as_the_hosts_own_IPixiePlugin_from_a_context_of_its_own()
    {
        Install();
        var catalog = NewCatalog();
        var enabled = new List<InstalledPlugin>();
        catalog.PluginEnabled += (_, p) => enabled.Add(p);

        catalog.Initialize();

        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal(PluginStatus.Loaded, plugin.Status);
        Assert.Null(plugin.Detail);
        Assert.Same(plugin, Assert.Single(enabled));
        Assert.NotNull(plugin.Instance);
        Assert.True(plugin.HasUi);

        // The plugin's own code lives in its own context…
        var context = AssemblyLoadContext.GetLoadContext(plugin.Instance!.GetType().Assembly);
        Assert.IsType<PluginLoadContext>(context);
        Assert.NotSame(AssemblyLoadContext.Default, context);
        Assert.Equal("plugin:hello", context!.Name);

        // …but the Sdk and the core it sees are the host's: one type identity on both sides of the boundary.
        Assert.Same(typeof(IPixiePlugin).Assembly, context.LoadFromAssemblyName(typeof(IPixiePlugin).Assembly.GetName()));
        Assert.Same(typeof(IYtDlpService).Assembly, context.LoadFromAssemblyName(typeof(IYtDlpService).Assembly.GetName()));
    }

    [Fact]
    public void Configure_got_a_host_that_knows_the_plugin_and_the_capability_is_reachable_through_it()
    {
        Install();
        var catalog = NewCatalog();
        catalog.Initialize();

        var host = catalog.Plugins[0].Host!;
        Assert.Equal("hello", host.Manifest.Id);
        Assert.Equal("Hello", host.Manifest.Name);
        Assert.False(host.ShutdownToken.IsCancellationRequested);

        Assert.True(host.TryGetCapability(GreetCapability, out var greet));
        Assert.Equal("Olá, Pixie!", Assert.IsType<Func<string, string>>(greet)("Pixie"));
        Assert.False(host.TryGetCapability("nobody.registered.this", out _));

        // data/<id>/ appears on first use, not on load.
        Assert.False(Directory.Exists(Path.Combine(_root, "data", "hello")));
        Assert.True(Directory.Exists(host.DataDirectory));
        Assert.Equal(Path.Combine(_root, "data", "hello"), host.DataDirectory);
    }

    [Fact]
    public void The_tab_view_is_XAML_built_inside_the_plugin_context()
    {
        Install();
        var catalog = NewCatalog();
        catalog.Initialize();
        var ui = Assert.IsAssignableFrom<IUiContribution>(catalog.Plugins[0].Instance);

        // InitializeComponent resolves "/Pixie.Hello;component/helloview.xaml" — an assembly WPF must find
        // even though it was never loaded into the default context.
        FrameworkElement? view = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { view = ui.CreateView(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
        Assert.Equal("Hello", ui.TabHeader);
        Assert.Equal("Pixie.Hello.HelloView", view!.GetType().FullName);
    }

    // ───── Disable / enable ─────

    [Fact]
    public void Disable_is_immediate_and_remembered_across_starts()
    {
        Install();
        var settings = new PluginSettings();
        var catalog = NewCatalog(settings);
        catalog.Initialize();
        var plugin = catalog.Plugins[0];
        var host = plugin.Host!;
        var disabled = new List<InstalledPlugin>();
        catalog.PluginDisabled += (_, p) => disabled.Add(p);

        catalog.Disable("hello");

        Assert.Equal(PluginStatus.Disabled, plugin.Status);
        Assert.Null(plugin.Instance);
        Assert.True(host.ShutdownToken.IsCancellationRequested);
        Assert.False(host.TryGetCapability(GreetCapability, out _));
        Assert.Same(plugin, Assert.Single(disabled));
        Assert.Equal(["hello"], settings.DisabledIds);

        // Next start, same settings: still off, and its assembly is never touched.
        var again = NewCatalog(settings);
        again.Initialize();
        var next = Assert.Single(again.Plugins);
        Assert.Equal(PluginStatus.Disabled, next.Status);
        Assert.Null(next.LoadContext);
        Assert.NotNull(next.Manifest);
    }

    [Fact]
    public void Enable_after_disable_is_a_fresh_instance_in_the_same_context()
    {
        Install();
        var settings = new PluginSettings();
        var catalog = NewCatalog(settings);
        catalog.Initialize();
        var plugin = catalog.Plugins[0];
        var first = plugin.Instance;
        var context = plugin.LoadContext;
        catalog.Disable("hello");

        Assert.True(catalog.Enable("hello"));

        Assert.Equal(PluginStatus.Loaded, plugin.Status);
        Assert.NotSame(first, plugin.Instance);
        Assert.Same(context, plugin.LoadContext);
        Assert.False(plugin.Host!.ShutdownToken.IsCancellationRequested);
        Assert.True(plugin.Host.TryGetCapability(GreetCapability, out _));   // registered again by the new Configure
        Assert.Empty(settings.DisabledIds);
    }

    // ───── Refusals (no code loaded) ─────

    [Fact]
    public void Incompatible_apiVersion_is_refused_before_any_code_loads()
    {
        Install("hello-future", Manifest("hello-future", apiVersion: "9.0"));
        var catalog = NewCatalog();

        catalog.Initialize();

        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal(PluginStatus.Refused, plugin.Status);
        Assert.Contains("apiVersion 9.0", plugin.Detail);
        Assert.NotNull(plugin.Manifest);   // the declared name is still there for the Plugins tab
        Assert.Null(plugin.LoadContext);
        Assert.DoesNotContain(AssemblyLoadContext.All, c => c.Name == "plugin:hello-future");
    }

    [Fact]
    public void Manifest_id_must_match_the_folder()
    {
        Install("elsewhere", Manifest("hello"));
        var catalog = NewCatalog();

        catalog.Initialize();

        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal(PluginStatus.Refused, plugin.Status);
        Assert.Contains("não bate com a pasta 'elsewhere'", plugin.Detail);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "id": "broken", "name": "Sem o resto" }""")]
    public void Broken_manifest_is_refused_with_the_parser_message(string json)
    {
        Install("broken", json);
        var catalog = NewCatalog();

        catalog.Initialize();

        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal(PluginStatus.Refused, plugin.Status);
        Assert.StartsWith("plugin.json inválido: ", plugin.Detail);
    }

    [Fact]
    public void A_missing_plugin_json_or_assembly_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(PluginsDir, "empty"));
        Install("ghost", Manifest("ghost").Replace("Pixie.Hello.dll", "Nope.dll"));
        var catalog = NewCatalog();

        catalog.Initialize();

        Assert.Equal(2, catalog.Plugins.Count);
        Assert.All(catalog.Plugins, p => Assert.Equal(PluginStatus.Refused, p.Status));
        Assert.Equal("sem plugin.json", catalog.Find("empty")!.Detail);
        Assert.Equal("'Nope.dll' não está na pasta", catalog.Find("ghost")!.Detail);
    }

    // ───── Dependencies ─────

    [Fact]
    public void Dependencies_order_the_load_and_travel_with_disable_and_enable()
    {
        Install();
        Install("aaa-dep", Manifest("aaa-dep", dependsOn: """["hello"]""", entryType: Quiet));   // sorts before "hello", loads after it
        Install("orphan", Manifest("orphan", dependsOn: """["nope"]""", entryType: Quiet));
        var catalog = NewCatalog();
        var loadOrder = new List<string>();
        catalog.PluginEnabled += (_, p) => loadOrder.Add(p.Id);

        catalog.Initialize();

        Assert.Equal(["hello", "aaa-dep"], loadOrder);
        var orphan = catalog.Find("orphan")!;
        Assert.Equal(PluginStatus.Refused, orphan.Status);
        Assert.Equal("depende de 'nope', que não está instalado", orphan.Detail);

        // Switching the dependency off takes the dependent down with it…
        catalog.Disable("hello");
        var dep = catalog.Find("aaa-dep")!;
        Assert.Equal(PluginStatus.Refused, dep.Status);
        Assert.Equal("depende de 'hello', que foi desabilitado", dep.Detail);
        Assert.Null(dep.Instance);

        // …and switching it back on brings the dependent back, since the user never disabled that one.
        Assert.True(catalog.Enable("hello"));
        Assert.Equal(PluginStatus.Loaded, dep.Status);
        Assert.NotNull(dep.Instance);
    }

    [Fact]
    public void Two_plugins_cannot_register_the_same_capability()
    {
        Install();
        Install("hello-twin", Manifest("hello-twin"));   // same HelloPlugin, so the same "hello.greet"
        var catalog = NewCatalog();

        catalog.Initialize();

        Assert.Equal(PluginStatus.Loaded, catalog.Find("hello")!.Status);
        var twin = catalog.Find("hello-twin")!;
        Assert.Equal(PluginStatus.Failed, twin.Status);
        Assert.Equal("A capacidade 'hello.greet' já está registrada pelo plugin 'hello'.", twin.Detail);
    }

    // ───── API 1.1: what the app tells the plugins ─────

    [Fact]
    public void Analysis_events_and_the_comments_request_reach_a_loaded_plugin_and_die_with_it()
    {
        Install("quiet", Manifest("quiet", entryType: Quiet));
        var catalog = NewCatalog();
        Assert.False(catalog.WantsAnalysisComments);   // nobody loaded yet

        catalog.Initialize();

        // QuietPlugin asked for comments in Configure, so the app's analyses will fetch them.
        Assert.True(catalog.WantsAnalysisComments);

        var url = "https://www.youtube.com/watch?v=abc";
        var info = new VideoUrlInfo { OriginalUrl = url, Video = new VideoInfo("abc", "Mix", null, null, null, url) };
        catalog.RaiseAnalysisCompleted(url, info);

        var host = catalog.Plugins[0].Host!;
        Assert.True(host.TryGetCapability("quiet.lastAnalysis", out var last));
        Assert.Equal(url, Assert.IsType<Func<string?>>(last)());

        // Disabled: no more events, no more comment requests, and the capability is gone.
        catalog.Disable("quiet");
        Assert.False(catalog.WantsAnalysisComments);
        catalog.RaiseAnalysisCompleted("https://www.youtube.com/watch?v=xyz", info);   // must not throw, must not reach it
        Assert.False(host.TryGetCapability("quiet.lastAnalysis", out _));
    }

    [Fact]
    public void A_throwing_event_handler_is_logged_and_does_not_reach_the_host()
    {
        Install("quiet", Manifest("quiet", entryType: Quiet));
        var catalog = NewCatalog();
        var logged = new List<LogEntry>();
        catalog.LogEmitted += (_, e) => logged.Add(e);
        catalog.Initialize();
        catalog.Plugins[0].Host!.DownloadCompleted += (_, _) => throw new InvalidOperationException("boom");

        var request = new DownloadRequest("https://x", _root, "%(title)s.%(ext)s", new AudioOptions(), new AdvancedOptions());
        catalog.RaiseDownloadCompleted(request, new DownloadResult("https://x", true, null, null, TimeSpan.Zero));

        var error = Assert.Single(logged, e => e.Level == LogLevel.Error);
        Assert.Equal("Plugin:quiet", error.Source);
        Assert.Contains("DownloadCompleted", error.Message);
        Assert.Contains("boom", error.Message);
    }

    // ───── Uninstall ─────

    [Fact]
    public void Uninstall_of_a_loaded_plugin_shuts_it_down_now_and_marks_the_folder()
    {
        Install();
        var settings = new PluginSettings();
        var catalog = NewCatalog(settings);
        catalog.Initialize();
        var plugin = catalog.Plugins[0];
        var host = plugin.Host!;
        var marker = Path.Combine(plugin.Directory, PluginCatalog.UninstallMarkerFileName);

        catalog.Uninstall("hello");

        Assert.Equal(PluginStatus.PendingUninstall, plugin.Status);
        Assert.Null(plugin.Instance);
        Assert.True(host.ShutdownToken.IsCancellationRequested);
        Assert.True(File.Exists(marker));
        Assert.False(catalog.Enable("hello"));   // not until the marker is gone

        catalog.CancelUninstall("hello");

        Assert.False(File.Exists(marker));
        Assert.Equal(PluginStatus.Disabled, plugin.Status);
        Assert.Equal(["hello"], settings.DisabledIds);
    }

    [Fact]
    public void The_next_start_deletes_a_folder_marked_for_uninstall()
    {
        // Installed but disabled, so nothing maps its files and the folder can actually go.
        var dir = Install();
        var settings = new PluginSettings { DisabledIds = { "hello" } };
        var catalog = NewCatalog(settings);
        catalog.Initialize();
        Assert.Equal(PluginStatus.Disabled, catalog.Plugins[0].Status);

        catalog.Uninstall("hello");
        Assert.True(File.Exists(Path.Combine(dir, PluginCatalog.UninstallMarkerFileName)));

        var nextStart = NewCatalog(settings);
        nextStart.Initialize();

        Assert.False(Directory.Exists(dir));
        Assert.Empty(nextStart.Plugins);
    }

    // ───── Helpers ─────

    /// <summary>Copies the built Hello plugin into <c>plugins/&lt;id&gt;/</c>, with the test's own manifest when given.</summary>
    private string Install(string id = "hello", string? manifestJson = null)
    {
        var dir = Path.Combine(PluginsDir, id);
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.GetFiles(HelloOutput))
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
        if (manifestJson is not null)
            File.WriteAllText(Path.Combine(dir, PluginCatalog.ManifestFileName), manifestJson);
        return dir;
    }

    private static string Manifest(string id, string apiVersion = "1.0", string dependsOn = "[]", string entryType = "Pixie.Hello.HelloPlugin") => $$"""
        {
          "id": "{{id}}",
          "name": "Hello",
          "version": "0.1.0",
          "apiVersion": "{{apiVersion}}",
          "assemblyFile": "Pixie.Hello.dll",
          "entryType": "{{entryType}}",
          "dependsOn": {{dependsOn}}
        }
        """;

    private PluginCatalog NewCatalog(PluginSettings? settings = null)
    {
        var service = new YtDlpService(logger: null, toolsDirectory: Path.Combine(_root, "tools"), cacheDirectory: Path.Combine(_root, "cache"));
        _disposables.Add(service);
        return new PluginCatalog(PluginsDir, Path.Combine(_root, "data"), service, settings ?? new PluginSettings(), logger: null);
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
        // A loaded plugin keeps its DLL mapped for the life of the process, so this often can't finish; the
        // leftovers are under the OS temp folder.
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
