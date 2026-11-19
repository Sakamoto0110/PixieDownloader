using YtDlpCore;

namespace YtDlpCore.Tests;

public class OutputLayoutTests
{
    [Theory]
    [InlineData(OutputFolder.None, OutputName.Title, "%(title)s.%(ext)s")]
    [InlineData(OutputFolder.Playlist, OutputName.IndexTitle, "%(playlist_title)s/%(playlist_index)02d - %(title)s.%(ext)s")]
    [InlineData(OutputFolder.Channel, OutputName.Title, "%(uploader)s/%(title)s.%(ext)s")]
    [InlineData(OutputFolder.None, OutputName.DateTitle, "%(upload_date)s - %(title)s.%(ext)s")]
    [InlineData(OutputFolder.ChannelPlaylist, OutputName.ChannelTitle, "%(uploader)s/%(playlist_title)s/%(uploader)s - %(title)s.%(ext)s")]
    public void Build_composes_folder_then_name_then_extension(OutputFolder folder, OutputName name, string expected)
    {
        Assert.Equal(expected, OutputLayout.Build(folder, name));
    }

    [Fact]
    public void TryMatch_reads_back_every_combination()
    {
        foreach (var f in Enum.GetValues<OutputFolder>())
        foreach (var n in Enum.GetValues<OutputName>())
        {
            Assert.True(OutputLayout.TryMatch(OutputLayout.Build(f, n), out var folder, out var name));
            Assert.Equal((f, n), (folder, name));
        }
    }

    [Theory]   // the four presets of the old "Organizar arquivos" box
    [InlineData("%(title)s.%(ext)s", OutputFolder.None, OutputName.Title)]
    [InlineData("%(playlist_title)s/%(playlist_index)02d - %(title)s.%(ext)s", OutputFolder.Playlist, OutputName.IndexTitle)]
    [InlineData("%(uploader)s/%(title)s.%(ext)s", OutputFolder.Channel, OutputName.Title)]
    [InlineData("%(upload_date)s - %(title)s.%(ext)s", OutputFolder.None, OutputName.DateTitle)]
    public void TryMatch_maps_the_old_presets(string template, OutputFolder folder, OutputName name)
    {
        Assert.True(OutputLayout.TryMatch(template, out var f, out var n));
        Assert.Equal((folder, name), (f, n));
    }

    [Theory]
    [InlineData("%(id)s.%(ext)s")]
    [InlineData("%(title)s")]
    [InlineData("")]
    [InlineData(null)]
    public void TryMatch_refuses_what_no_combination_makes(string? template)
    {
        Assert.False(OutputLayout.TryMatch(template, out _, out _));
    }

    [Theory]
    [InlineData("%(uploader)s/%(title)s", "%(uploader)s/%(title)s.%(ext)s")]
    [InlineData("%(title)s.%(ext)s", "%(title)s.%(ext)s")]        // typed the extension: not doubled
    [InlineData("  Mix/%(title)s  ", "Mix/%(title)s.%(ext)s")]
    [InlineData("Mix/", "Mix.%(ext)s")]
    [InlineData("", "%(title)s.%(ext)s")]
    [InlineData("   ", "%(title)s.%(ext)s")]
    [InlineData(null, "%(title)s.%(ext)s")]
    public void FromCustom_always_ends_in_exactly_one_extension(string? custom, string expected)
    {
        Assert.Equal(expected, OutputLayout.FromCustom(custom));
    }
}

public class OutputTemplateRenderTests
{
    private static readonly Dictionary<string, string?> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = "AC/DC: Live?",
        ["uploader"] = "Canal",
        ["id"] = "abc",
        ["ext"] = "mp3",
    };

    private static string Missing(string field) => $"<{field}>";

    [Fact]
    public void Render_splits_folders_from_the_file_and_sanitizes_values_like_yt_dlp()
    {
        var r = OutputTemplate.Render("%(uploader)s/%(title)s.%(ext)s", Values, Missing);
        Assert.Equal(["Canal"], r.Folders);
        Assert.Equal("AC⧸DC： Live？.mp3", r.FileName);   // the "/" in the title is not a folder
    }

    [Fact]
    public void Render_names_a_missing_field_and_honors_a_default()
    {
        Assert.Equal("<upload_date> - abc.mp3", OutputTemplate.Render("%(upload_date)s - %(id)s.%(ext)s", Values, Missing).FileName);
        Assert.Equal("sem data - abc.mp3", OutputTemplate.Render("%(upload_date|sem data)s - %(id)s.%(ext)s", Values, Missing).FileName);
    }

    [Fact]
    public void Render_pads_truncates_and_unescapes_percent()
    {
        var values = new Dictionary<string, string?> { ["n"] = "7", ["title"] = "abcdefgh", ["ext"] = "mp3" };
        Assert.Equal("007 - abc 100%.mp3", OutputTemplate.Render("%(n)03d - %(title).3s 100%%.%(ext)s", values, Missing).FileName);
    }

    [Fact]
    public void Render_drops_empty_and_trailing_dot_components_like_Windows()
    {
        var values = new Dictionary<string, string?> { ["album"] = "Vol. 2...", ["title"] = "t", ["ext"] = "mp3" };
        var r = OutputTemplate.Render("%(album)s//%(title)s.%(ext)s", values, Missing);
        Assert.Equal(["Vol. 2"], r.Folders);
        Assert.Equal("t.mp3", r.FileName);
    }

    [Fact]
    public void ForPlaylistItem_bakes_title_and_padded_index_and_render_reads_them_back()
    {
        var tpl = OutputTemplate.ForPlaylistItem(OutputLayout.Build(OutputFolder.Playlist, OutputName.IndexTitle), "100% Hits", 7);
        Assert.Equal("100%% Hits/07 - %(title)s.%(ext)s", tpl);
        var r = OutputTemplate.Render(tpl, Values, Missing);
        Assert.Equal(["100% Hits"], r.Folders);
        Assert.StartsWith("07 - ", r.FileName);
    }

    [Theory]
    [InlineData("%(playlist_title)s/%(title)s.%(ext)s", "playlist_title", true)]
    [InlineData("%(playlist_index)02d - %(title)s.%(ext)s", "playlist_index", true)]
    [InlineData("%(playlist_title|x)s - %(title)s.%(ext)s", "playlist_title", true)]
    [InlineData("%%(playlist_title)s %(title)s.%(ext)s", "playlist_title", false)]   // escaped: literal text
    [InlineData("%(title)s.%(ext)s", "playlist_title", false)]
    public void UsesField_sees_only_real_fields(string template, string field, bool expected)
    {
        Assert.Equal(expected, OutputTemplate.UsesField(template, field));
    }
}

public class PathSettingsLayoutTests
{
    [Fact]
    public void Template_follows_the_selectors_and_LastTemplate_mirrors_it()
    {
        var p = new PathSettings();
        p.Folder = OutputFolder.Channel;
        p.Name = OutputName.DateTitle;
        Assert.Equal("%(uploader)s/%(upload_date)s - %(title)s.%(ext)s", p.Template);
        Assert.Equal(p.Template, p.LastTemplate);
    }

    [Fact]
    public void Turning_custom_on_starts_from_the_selectors_and_off_keeps_the_text()
    {
        var p = new PathSettings { Folder = OutputFolder.Channel, Name = OutputName.Title };
        p.UseCustomTemplate = true;
        Assert.Equal("%(uploader)s/%(title)s", p.CustomTemplate);

        p.CustomTemplate = "%(uploader)s/%(id)s";
        Assert.Equal("%(uploader)s/%(id)s.%(ext)s", p.Template);

        p.UseCustomTemplate = false;
        Assert.Equal("%(uploader)s/%(title)s.%(ext)s", p.Template);
        p.UseCustomTemplate = true;
        Assert.Equal("%(uploader)s/%(id)s", p.CustomTemplate);   // not overwritten by the selectors
    }

    [Fact]
    public void AdoptLegacyTemplate_picks_selectors_or_falls_back_to_custom()
    {
        var preset = new PathSettings();
        preset.AdoptLegacyTemplate("%(uploader)s/%(title)s.%(ext)s");
        Assert.False(preset.UseCustomTemplate);
        Assert.Equal((OutputFolder.Channel, OutputName.Title), (preset.Folder, preset.Name));

        var custom = new PathSettings();
        custom.AdoptLegacyTemplate("%(uploader)s - %(id)s.%(ext)s");
        Assert.True(custom.UseCustomTemplate);
        Assert.Equal("%(uploader)s - %(id)s", custom.CustomTemplate);
        Assert.Equal("%(uploader)s - %(id)s.%(ext)s", custom.Template);
    }
}

public sealed class SettingsUpgradeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pixie-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsUpgradeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private async Task<AppSettings> LoadAsync(string json)
    {
        var path = Path.Combine(_dir, "settings.json");
        await File.WriteAllTextAsync(path, json);
        using var service = new SettingsService(path);
        await service.LoadAsync();
        return service.Current;
    }

    [Fact]
    public async Task A_version_1_file_with_a_custom_template_becomes_the_custom_layout()
    {
        var s = await LoadAsync("""{ "version": 1, "paths": { "lastOutputDirectory": "C:\\Music", "lastTemplate": "%(id)s/%(title)s.%(ext)s" } }""");
        Assert.Equal(AppSettings.CurrentVersion, s.Version);
        Assert.True(s.Paths.UseCustomTemplate);
        Assert.Equal("%(id)s/%(title)s", s.Paths.CustomTemplate);
        Assert.Equal("%(id)s/%(title)s.%(ext)s", s.Paths.Template);
        Assert.Equal(@"C:\Music", s.Paths.LastOutputDirectory);
    }

    [Fact]
    public async Task A_version_1_file_with_a_preset_becomes_the_selectors()
    {
        var s = await LoadAsync("""{ "version": 1, "paths": { "lastTemplate": "%(title)s.%(ext)s" } }""");
        Assert.False(s.Paths.UseCustomTemplate);
        Assert.Equal((OutputFolder.None, OutputName.Title), (s.Paths.Folder, s.Paths.Name));
    }

    [Fact]
    public async Task A_value_from_a_newer_build_reads_as_the_default_instead_of_failing_the_file()
    {
        var s = await LoadAsync("""{ "version": 2, "ui": { "debugPanelEnabled": true }, "paths": { "folder": "SomethingNew", "name": "Title" } }""");
        Assert.True(s.Ui.DebugPanelEnabled);                 // the rest of the file survived
        Assert.Equal(OutputFolder.Playlist, s.Paths.Folder);  // the default, declared first
        Assert.Equal(OutputName.Title, s.Paths.Name);
    }

    [Fact]
    public async Task Version_2_round_trips_the_layout()
    {
        var path = Path.Combine(_dir, "settings.json");
        using (var service = new SettingsService(path))
        {
            await service.LoadAsync();
            service.Current.Paths.Folder = OutputFolder.ChannelPlaylist;
            service.Current.Paths.Name = OutputName.DateTitle;
            service.Current.Paths.UseCustomTemplate = true;
            service.Current.Paths.CustomTemplate = "%(id)s";
            service.Current.Paths.UseCustomTemplate = false;
            await service.SaveAsync();
        }
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"folder\": \"ChannelPlaylist\"", json);

        using var again = new SettingsService(path);
        await again.LoadAsync();
        var p = again.Current.Paths;
        Assert.Equal((OutputFolder.ChannelPlaylist, OutputName.DateTitle, false, "%(id)s"), (p.Folder, p.Name, p.UseCustomTemplate, p.CustomTemplate));
        Assert.Equal(p.Template, p.LastTemplate);
    }
}

public class TitleCleanupTests
{
    [Theory]
    [InlineData("Oncologia Veterinária SP 18.07.mp4", "Oncologia Veterinária SP 18.07")]
    [InlineData("Aula 3.MP4", "Aula 3")]
    [InlineData("video.mp4.mp4", "video")]
    [InlineData("show.mkv  ", "show")]
    [InlineData("track.m4a", "track")]
    [InlineData("Versão 2.0", "Versão 2.0")]            // not a media extension
    [InlineData("Ep. 3.mp4 ao vivo", "Ep. 3.mp4 ao vivo")] // not at the end
    [InlineData(".mp4", ".mp4")]                        // nothing before it: left alone
    [InlineData("", "")]
    public void StripMediaExtension_cuts_only_trailing_media_extensions(string title, string expected)
    {
        Assert.Equal(expected, TitleCleanup.StripMediaExtension(title));
    }

    [Fact]
    public void YtDlpArgs_carry_the_same_pattern_for_the_title_field()
    {
        Assert.Equal(["--replace-in-metadata", "title", TitleCleanup.MediaExtensionPattern, ""], TitleCleanup.YtDlpArgs);
    }
}

public class UndoHistoryTests
{
    [Fact]
    public void Undo_and_redo_walk_the_recorded_values()
    {
        var h = new UndoHistory<string>();
        var current = "a";
        h.Record(current); current = "ab";
        h.Record(current); current = "abc";

        current = h.Undo(current);
        Assert.Equal("ab", current);
        current = h.Undo(current);
        Assert.Equal("a", current);
        Assert.False(h.CanUndo);

        current = h.Redo(current);
        Assert.Equal("ab", current);
        current = h.Redo(current);
        Assert.Equal("abc", current);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void A_new_step_after_an_undo_drops_the_redo_side()
    {
        var h = new UndoHistory<string>();
        h.Record("a");
        var current = h.Undo("b");
        Assert.True(h.CanRedo);
        h.Record(current);
        Assert.False(h.CanRedo);
        Assert.Equal("a", h.Undo("c"));
    }

    [Fact]
    public void Past_the_limit_the_oldest_step_is_forgotten()
    {
        var h = new UndoHistory<int>(limit: 2);
        h.Record(1); h.Record(2); h.Record(3);
        Assert.Equal(3, h.Undo(4));
        Assert.Equal(2, h.Undo(3));
        Assert.False(h.CanUndo);
    }
}

public class OutputTemplateRenderSyntaxTests
{
    private static string File(string template, Dictionary<string, string?> values)
        => OutputTemplate.Render(template, values, f => $"<{f}>").FileName;

    [Fact]
    public void Alternatives_take_the_first_field_with_a_value()
    {
        Assert.Equal("Chan.mp3", File("%(artist,uploader)s.%(ext)s", new() { ["uploader"] = "Chan", ["ext"] = "mp3" }));
        Assert.Equal("Art.mp3", File("%(artist,uploader)s.%(ext)s", new() { ["artist"] = "Art", ["uploader"] = "Chan", ["ext"] = "mp3" }));
        Assert.Equal("<artist>.mp3", File("%(artist,uploader)s.%(ext)s", new() { ["ext"] = "mp3" }));
    }

    [Fact]
    public void Date_formats_apply_per_alternative_and_to_unix_times()
    {
        var values = new Dictionary<string, string?> { ["upload_date"] = "20230718", ["ext"] = "mp3" };
        Assert.Equal("2023-07-18.mp3", File("%(upload_date>%Y-%m-%d)s.%(ext)s", values));
        Assert.Equal("2023.mp3", File("%(release_date>%Y,upload_date>%Y|sem ano)s.%(ext)s", values));
        Assert.Equal("sem ano.mp3", File("%(release_date>%Y|sem ano)s.%(ext)s", values));

        var epoch = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var expected = epoch.LocalDateTime.ToString("yyyy-MM-dd") + ".mp3";
        Assert.Equal(expected, File("%(epoch>%Y-%m-%d)s.%(ext)s", new() { ["epoch"] = epoch.ToUnixTimeSeconds().ToString(), ["ext"] = "mp3" }));
    }

    [Fact]
    public void Replacement_stands_in_for_a_present_value_only()
    {
        Assert.Equal("[HD] t.mp4", File("%(height&[HD] )st.%(ext)s", new() { ["height"] = "1080", ["ext"] = "mp4" }));
        Assert.Equal("t.mp4", File("%(height&[HD] |)st.%(ext)s", new() { ["ext"] = "mp4" }));
    }
}
