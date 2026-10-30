using System.IO;
using Pixie.Library.Catalog;
using YtDlpCore;

namespace Pixie.Library.Tests;

/// <summary>The runner end to end: manifest flag, index on disk, coalescing, the swap that fails, the run that dies, the plan that changed.</summary>
public sealed class LibrarySyncTests : IDisposable
{
    private readonly TempTree _music = new("music");
    private readonly TempTree _data = new("data");
    private readonly FakeTagReader _tags = new();
    private readonly FakeIdentity _identity = new();
    private readonly List<string> _log = [];
    private readonly LibraryStore _store;
    private readonly LibraryManifest _manifest;

    public LibrarySyncTests()
    {
        _store = new LibraryStore(_data.Root);
        _manifest = LibraryManifest.Load(_data.Root, _music.Root);   // the music tree is the storage root
    }

    private LibrarySync NewSync(CancellationToken shutdown = default)
        => new(_manifest, _store, () => ScanPlan.Build(_manifest.Snapshot(), _music.Root), _tags.Read, _identity.Get, (level, message, _) => _log.Add($"{level}: {message}"), shutdown);

    [Fact]
    public async Task A_pending_manifest_scans_writes_the_index_and_lands_on_synced()
    {
        _music.Mp3("a.mp3");
        var sync = NewSync();
        var loaded = new List<IndexData>();
        var completed = new List<ScanResult>();
        sync.IndexLoaded += loaded.Add;
        sync.Completed += completed.Add;

        await sync.Request(SyncMode.Incremental);

        Assert.Single(loaded);
        Assert.Empty(loaded[0].Entries);   // the previous index: nothing yet
        var result = Assert.Single(completed);
        Assert.Equal(1, result.Added);
        Assert.Equal(SyncStatus.Synced, _manifest.Status);
        Assert.NotNull(_manifest.LastSync);
        Assert.True(File.Exists(_store.IndexPath));
        Assert.EndsWith("index.v2.json", _store.IndexPath);
        Assert.Single(_store.ReadIndex().Entries);
        Assert.Single(sync.Index!.Entries);
        Assert.NotNull(sync.Index.ScannedAt);
        Assert.Equal(LibraryScanner.RulesVersion, sync.Index.RulesVersion);
        Assert.False(sync.IsRunning);
    }

    [Fact]
    public async Task Synced_with_nothing_changed_does_not_scan_again()
    {
        _music.Mp3("a.mp3");
        var sync = NewSync();
        var completed = 0;
        sync.Completed += _ => completed++;
        await sync.Request(SyncMode.Incremental);
        _tags.Calls.Clear();

        await sync.Request(SyncMode.Incremental);

        Assert.Equal(1, completed);
        Assert.Empty(_tags.Calls);
    }

    [Fact]
    public async Task A_full_request_scans_even_when_synced()
    {
        _music.Mp3("a.mp3");
        var sync = NewSync();
        var results = new List<ScanResult>();
        sync.Completed += results.Add;
        await sync.Request(SyncMode.Incremental);

        await sync.Request(SyncMode.Full);

        Assert.Equal(2, results.Count);
        Assert.True(results[1].Full);
    }

    [Fact]
    public async Task A_changed_scan_plan_makes_the_next_run_a_full_one()
    {
        // A second folder joins the catalogue: its tree is new, so the incremental walk lists it whole; nothing
        // else is relisted, and the run is not a full one.
        _music.Mp3("a.mp3");
        using var other = new TempTree("other");
        other.Mp3("x.mp3");
        var sync = NewSync();
        var results = new List<ScanResult>();
        sync.Completed += results.Add;
        await sync.Request(SyncMode.Incremental);
        Assert.Single(results[0].Index.Entries);

        _manifest.Update(m => m.Items.Add(Items.SourceFolder(other.Root, "Outra")));
        await sync.Request(SyncMode.Incremental);

        Assert.Equal(2, results.Count);
        Assert.False(results[1].Full);
        Assert.Equal(2, results[1].Index.Entries.Count);
        Assert.Equal(2, results[1].Index.Sources.Count);

        // Renaming the source changes nothing the scanner sees: no scan.
        _manifest.Update(m => m.Items[0].Source!.Name = "Renomeada");
        await sync.Request(SyncMode.Incremental);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task A_flip_during_the_scan_makes_the_run_go_again_and_still_end_synced()
    {
        _music.Mp3("a.mp3");
        var sync = NewSync();
        var results = new List<ScanResult>();
        sync.Completed += results.Add;
        var flipped = false;
        _tags.OnRead = _ =>
        {
            if (flipped)
                return;
            flipped = true;
            _music.Mp3("late.mp3");   // a download lands while we read tags…
            _manifest.MarkPending();  // …and flips the flag
        };

        await sync.Request(SyncMode.Incremental);

        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[1].Added);   // the second pass picked up late.mp3, incrementally
        Assert.False(results[1].Full);
        Assert.Equal(SyncStatus.Synced, _manifest.Status);
        Assert.Equal(2, sync.Index!.Entries.Count);
    }

    [Fact]
    public async Task The_first_scan_of_a_fresh_index_is_a_full_one()
    {
        _music.Mp3("a.mp3");
        var sync = NewSync();
        var results = new List<ScanResult>();
        sync.Completed += results.Add;

        await sync.Request(SyncMode.Incremental);

        Assert.True(Assert.Single(results).Full);   // an index without a plan is an index behind every plan
    }

    [Fact]
    public async Task A_request_during_a_run_is_queued_and_runs_after()
    {
        _music.Mp3("a.mp3");
        var sync = NewSync();
        await sync.Request(SyncMode.Incremental);   // an index with the plan in it: from here on, incremental means incremental
        var results = new List<ScanResult>();
        sync.Completed += results.Add;
        _music.Mp3("b.mp3");
        using var reading = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _tags.OnRead = _ =>
        {
            reading.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        var first = sync.Request(SyncMode.Incremental);
        Assert.True(reading.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(sync.IsRunning);
        var second = sync.Request(SyncMode.Full);
        Assert.Same(first, second);   // one run, the request rides on it
        _tags.OnRead = null;
        release.Set();
        await first;

        Assert.Equal(2, results.Count);
        Assert.False(results[0].Full);
        Assert.True(results[1].Full);
        Assert.False(sync.IsRunning);
    }

    [Fact]
    public async Task The_inbox_is_folded_in_and_emptied()
    {
        var fresh = _music.Mp3("fresh.mp3");
        _store.Inbox.Append(fresh);
        _store.Inbox.Append(@"D:\nowhere\gone.mp3");   // a block whose file is not under any source: harmless
        var sync = NewSync();
        var ready = new List<FileNode>();
        sync.Progress += p => { if (p.Ready is not null) ready.AddRange(p.Ready); };

        await sync.Request(SyncMode.Incremental);

        Assert.Equal([fresh], ready.Select(e => e.Path));
        Assert.Empty(_store.Inbox.ReadAll());
        Assert.True(Directory.Exists(_data.Full("inbox.v2")));
        Assert.False(Directory.Exists(_data.Full("inbox")));   // the 1.x folder is never created by the 2.x
        Assert.Single(sync.Index!.Entries);
    }

    [Fact]
    public async Task A_run_cut_short_leaves_failed_and_the_next_one_is_full()
    {
        _music.Mp3("a.mp3");
        _music.Mp3("b.mp3");
        using var shutdown = new CancellationTokenSource();
        var sync = NewSync(shutdown.Token);
        var completed = 0;
        sync.Completed += _ => completed++;
        _tags.OnRead = _ => shutdown.Cancel();   // the plugin is disabled mid-scan

        await sync.Request(SyncMode.Incremental);

        Assert.Equal(0, completed);
        Assert.Equal(SyncStatus.Failed, _manifest.Status);
        Assert.False(File.Exists(_store.IndexPath));   // nothing half-written
        Assert.False(sync.IsRunning);
        Assert.Equal(Task.CompletedTask, sync.Request(SyncMode.Incremental));   // shut down: no more runs

        // Next session: a new runner, a live token — the flag says failed, so the run is a full one.
        _tags.OnRead = null;
        var next = NewSync();
        var results = new List<ScanResult>();
        next.Completed += results.Add;
        await next.Request(SyncMode.Incremental);
        var result = Assert.Single(results);
        Assert.True(result.Full);
        Assert.Equal(SyncStatus.Synced, _manifest.Status);
    }

    [Fact]
    public async Task A_handler_that_throws_is_logged_and_does_not_break_the_run()
    {
        _music.Mp3("a.mp3");
        var sync = NewSync();
        sync.Completed += _ => throw new InvalidOperationException("boom");

        await sync.Request(SyncMode.Incremental);

        Assert.Equal(SyncStatus.Synced, _manifest.Status);
        Assert.Contains(_log, l => l.Contains("boom"));
    }

    [Fact]
    public async Task EnsureLoaded_reads_the_index_without_scanning()
    {
        _store.WriteIndex(new TestIndex(ScanPlan.Build(_manifest.Snapshot(), _music.Root)).File(_music.Full("old.mp3"), 1).Build());
        var sync = NewSync();
        var loaded = new List<IndexData>();
        sync.IndexLoaded += loaded.Add;

        await sync.EnsureLoadedAsync();
        await sync.EnsureLoadedAsync();

        Assert.Single(loaded);
        Assert.Single(sync.Index!.Entries);
        Assert.Equal(SyncStatus.Pending, _manifest.Status);
        Assert.Empty(_tags.Calls);
    }

    public void Dispose()
    {
        _music.Dispose();
        _data.Dispose();
    }
}
