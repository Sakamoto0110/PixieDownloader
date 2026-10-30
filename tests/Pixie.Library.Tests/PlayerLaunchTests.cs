using System.Runtime.InteropServices;
using Pixie.Library.Player;

namespace Pixie.Library.Tests;

/// <summary>
/// What the tab hands the player: VLC plays in the VLC already running — handed over when it takes files, closed
/// and reopened when it does not — and any other program gets the path alone.
/// </summary>
public sealed class PlayerLaunchTests
{
    private const string Player = @"D:\app\tools\vlc\vlc.exe";
    private const string Installed = @"C:\Program Files\VideoLAN\VLC\vlc.exe";
    private static readonly DateTime T0 = new(2026, 10, 2, 14, 0, 0);

    // ───── Arguments ─────

    [Theory]
    [InlineData(Installed)]
    [InlineData(@"D:\app\tools\vlc\VLC.EXE")]
    public void VLC_gets_the_running_instance_and_plays_now(string player)
    {
        var start = PlayerLaunch.For(player, @"C:\Music\mix ｜ live.mp3");

        Assert.Equal(player, start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal(["--one-instance", "--no-playlist-enqueue", @"C:\Music\mix ｜ live.mp3"], start.ArgumentList);
    }

    [Theory]
    [InlineData(@"C:\Program Files\foobar2000\foobar2000.exe")]
    [InlineData(@"C:\tools\vlc-helper.exe")]
    public void Any_other_player_gets_the_path_alone(string player)
    {
        var start = PlayerLaunch.For(player, @"C:\Music\a.mp3");

        Assert.Equal([@"C:\Music\a.mp3"], start.ArgumentList);
    }

    // ───── Plan ─────

    [Fact]
    public void No_VLC_running_starts_the_player()
        => Assert.Equal((Player, (RunningVlc?)null), PlayerLaunch.Plan(Player, []));

    [Fact]
    public void A_VLC_that_takes_files_gets_it_through_its_own_exe()
    {
        var open = new RunningVlc(7, Installed, AcceptsFiles: true, T0);

        Assert.Equal((Installed, (RunningVlc?)null), PlayerLaunch.Plan(Player, [open]));
    }

    [Fact]
    public void One_that_takes_files_wins_over_a_newer_one_that_does_not()
    {
        var takes = new RunningVlc(7, Installed, AcceptsFiles: true, T0);
        var newer = new RunningVlc(8, Player, AcceptsFiles: false, T0.AddMinutes(5));

        Assert.Equal((Installed, (RunningVlc?)null), PlayerLaunch.Plan(Player, [newer, takes]));
    }

    [Fact]
    public void A_VLC_that_does_not_take_files_is_closed_and_its_exe_reopened()
    {
        var older = new RunningVlc(7, Player, AcceptsFiles: false, T0);
        var newest = new RunningVlc(8, Installed, AcceptsFiles: false, T0.AddMinutes(5));

        Assert.Equal((Installed, newest), PlayerLaunch.Plan(Player, [older, newest]));
    }

    [Fact]
    public void An_exe_Windows_would_not_name_falls_back_to_the_player()
    {
        Assert.Equal((Player, (RunningVlc?)null), PlayerLaunch.Plan(Player, [new RunningVlc(7, null, true, T0)]));
        var closed = new RunningVlc(8, null, false, T0);
        Assert.Equal((Player, closed), PlayerLaunch.Plan(Player, [closed]));
    }

    // ───── The running processes, for real ─────

    [Fact]
    public void A_window_titled_like_VLC_s_IPC_one_marks_its_process()
    {
        var self = Environment.ProcessId;
        Assert.DoesNotContain(self, VlcInstances.WithIpcWindow(new HashSet<int> { self }));

        var window = CreateWindowExW(0, "STATIC", VlcInstances.IpcWindowPrefix + "9.9.9", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, window);
        try
        {
            Assert.Contains(self, VlcInstances.WithIpcWindow(new HashSet<int> { self }));
            Assert.Empty(VlcInstances.WithIpcWindow(new HashSet<int> { self + 1 }));   // only the processes asked about
        }
        finally
        {
            DestroyWindow(window);
        }
    }

    [Fact]
    public void The_exe_of_a_process_is_read_with_least_access()
    {
        Assert.Equal(Environment.ProcessPath, VlcInstances.ExePath(Environment.ProcessId), StringComparer.OrdinalIgnoreCase);
        Assert.Null(VlcInstances.ExePath(-1));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);
}
