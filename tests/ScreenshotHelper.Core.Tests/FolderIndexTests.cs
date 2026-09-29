using ScreenshotHelper.Core.Folder;
using ScreenshotHelper.Core.Session;

namespace ScreenshotHelper.Core.Tests;

public class FolderIndexTests
{
    [Fact]
    public void Scan_orders_by_numbering_and_ignores_foreign_and_temp_files()
    {
        using var folder = new TempFolder().With("2-1.png", "10.png", "2.png", "2-10 (x).png", "2-2.png", "20250101_123456.png", "notes.txt", ".~sshelper-abc.tmp");

        var index = FolderIndex.Scan(folder.Path);

        Assert.Equal(["2.png", "2-1.png", "2-2.png", "2-10 (x).png", "10.png"], index.Shots.Select(s => s.FileName));
        Assert.Equal(10, index.HighestMain);
        Assert.Equal("10.png", index.HighestShot!.FileName);
        Assert.True(index.ContainsFileName("NOTES.TXT"));
    }

    [Fact]
    public void Next_free_numbers_skip_taken_ones()
    {
        var index = FolderIndex.FromFileNames("/f", ["1.png", "2-1.png", "2-2 (a).png", "2-4.png", "3.png"]);

        Assert.Equal(4, index.NextFreeMain(2));
        Assert.Equal(5, index.NextFreeMain(5));
        Assert.Equal(3, index.NextFreeSub(2, 1));
        Assert.Equal(5, index.NextFreeSub(2, 4));
        Assert.Equal(4, index.HighestSub(2));
        Assert.True(index.NumberExists(2, 2));
        Assert.False(index.NumberExists(2, 3));
    }
}

public class StartAdvisorTests
{
    [Fact]
    public void Empty_folder_starts_at_one()
    {
        var defaults = StartAdvisor.Defaults(FolderIndex.FromFileNames("/f", []));

        Assert.Equal(new StartDefaults(1, 1, 1, null), defaults);
    }

    [Fact]
    public void Defaults_follow_the_highest_shot()
    {
        var defaults = StartAdvisor.Defaults(FolderIndex.FromFileNames("/f", ["7-4 (login).png", "7-2.png", "3.png"]));

        Assert.Equal(8, defaults.NewGroupMain);
        Assert.Equal(7, defaults.ContinueMain);
        Assert.Equal(5, defaults.ContinueSub);
        Assert.Equal("7-4 (login).png", defaults.HighestShot!.FileName);
    }

    [Fact]
    public void A_lone_solo_group_continues_at_two()
    {
        var defaults = StartAdvisor.Defaults(FolderIndex.FromFileNames("/f", ["4 (x).png"]));

        Assert.Equal(2, defaults.ContinueSub);
    }

    [Fact]
    public void New_group_collision_suggests_next_free()
    {
        var check = StartAdvisor.Check(FolderIndex.FromFileNames("/f", ["5.png", "6-1.png"]), StartMode.NewGroup, 5, 1);

        Assert.True(check.IsWarning);
        Assert.Equal(7, check.SuggestedMain);
    }

    [Fact]
    public void Continue_collision_suggests_next_free_sub()
    {
        var check = StartAdvisor.Check(FolderIndex.FromFileNames("/f", ["5-1.png", "5-2.png"]), StartMode.ContinueGroup, 5, 2);

        Assert.True(check.IsWarning);
        Assert.Equal(3, check.SuggestedSub);
    }

    [Fact]
    public void Invalid_numbers_block_starting()
    {
        var index = FolderIndex.FromFileNames("/f", []);

        Assert.True(StartAdvisor.Check(index, StartMode.NewGroup, -1, 1).IsBlocking);
        Assert.True(StartAdvisor.Check(index, StartMode.ContinueGroup, 1, 0).IsBlocking);
        Assert.Same(StartCheck.Ok, StartAdvisor.Check(index, StartMode.NewGroup, 1, 1));
    }
}
