using System.IO;
using Pixie.Library.Catalog;
using YtDlpCore;

namespace Pixie.Library.Tests;

/// <summary>The runner end to end: manifest flag, index on disk, coalescing, the swap that fails, the run that dies.</summary>
public sealed class LibrarySyncTests : IDisposable
{
    private readonly TempTree _music = new("music");
    private readonly TempTree _data = new("data");
    private readonly FakeTagReader _tags = new();
    private readonly List<string> _log = [];
    private readonly LibraryStore _store;
    private readonly LibraryManifest _manifest;

    public LibrarySyncTests()
    {
        _store = new LibraryStore(_data.Root);
        _manifest = LibraryManifest.Load(_store.ManifestPath);
        _manifest.Update(m => m.Roots.Add(new LibraryRoot("Músicas", _music.Root)));
    }

    private LibrarySync NewSync(CancellationToken shutdown = default)
        => new(_manifest, _store, _tags.Read, (level, message, _) => _log.Add($"{level}: {message}"), shutdown);

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
        Assert.Single(_store.ReadIndex().Entries);
        Assert.Single(sync.Index!.Entries);
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
        Assert.Equal(SyncStatus.Synced, _manifest.Status);
        Assert.Equal(2, sync.Index!.Entries.Count);
    }

    [Fact]
    public async Task A_request_during_a_run_is_queued_and_runs_after()
    {
        _music.Mp3("a.mp3");
        var sync = NewSync();
        var results = new List<ScanResult>();
        sync.Completed += results.Add;
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
        _store.Inbox.Append(@"D:\nowhere\gone.mp3");   // a block whose file is not under any root: harmless
        var sync = NewSync();
        var ready = new List<LibraryEntry>();
        sync.Progress += p => { if (p.Ready is not null) ready.AddRange(p.Ready); };

        await sync.Request(SyncMode.Incremental);

        Assert.Equal([fresh], ready.Select(e => e.Path));
        Assert.Empty(_store.Inbox.ReadAll());
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
        _store.WriteIndex(new IndexData { Entries = [new LibraryEntry { Path = _music.Full("old.mp3"), Size = 1, Modified = DateTime.UtcNow, Created = DateTime.UtcNow }] });
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
