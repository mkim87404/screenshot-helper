using System.Text.Json;
using ScreenshotHelper.Core.Diagnostics;
using ScreenshotHelper.Core.Folder;
using ScreenshotHelper.Core.Renumber;

namespace ScreenshotHelper.Core.Tests;

public class RenumberPlannerTests
{
    [Fact]
    public void Shift_example_from_the_spec_with_sub_zero_warning()
    {
        using var folder = new TempFolder().With("1-1.png", "1-2 (b).png", "1-3.png");
        var plan = Plan(folder, new ShiftOperation(2, -1), "1-1.png", "1-2 (b).png", "1-3.png");

        Assert.True(plan.IsValid);
        Assert.Equal(["3-0.png", "3-1 (b).png", "3-2.png"], plan.Rows.Select(r => r.Target));
        Assert.Equal(PlanRowStatus.Warning, plan.Rows[0].Status);
    }

    [Fact]
    public void Negative_shift_over_adjacent_groups_executes_lowest_first()
    {
        using var folder = new TempFolder().With("3.png", "4-1 (x).png", "4-2.png", "5.png");
        var plan = Plan(folder, new ShiftOperation(-1, 0), "3.png", "4-1 (x).png", "4-2.png", "5.png");

        Assert.True(plan.IsValid);
        Execute(folder, plan);

        Assert.Equal(["2.png", "3-1 (x).png", "3-2.png", "4.png"], folder.Names());
        Assert.Equal("4-1 (x).png", folder.Read("3-1 (x).png"));
    }

    [Fact]
    public void Positive_shift_over_a_chain_executes_highest_first()
    {
        using var folder = new TempFolder().With("1-1.png", "1-2.png", "1-3.png");
        var plan = Plan(folder, new ShiftOperation(0, 1), "1-1.png", "1-2.png", "1-3.png");

        Execute(folder, plan);

        Assert.Equal(["1-2.png", "1-3.png", "1-4.png"], folder.Names());
        Assert.Equal("1-1.png", folder.Read("1-2.png"));
        Assert.Equal("1-3.png", folder.Read("1-4.png"));
    }

    [Fact]
    public void Collision_with_an_unselected_file_is_a_conflict()
    {
        using var folder = new TempFolder().With("3-1.png", "1-1 (other).png");
        var plan = Plan(folder, new ShiftOperation(-2, 0), "3-1.png");

        Assert.False(plan.IsValid);
        Assert.Contains("1-1 (other).png", plan.Rows[0].Message, StringComparison.Ordinal);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void Moving_onto_a_number_with_a_different_tail_is_a_conflict()
    {
        using var folder = new TempFolder().With("1-1.png", "2-1 (x).png");

        Assert.True(Plan(folder, new MoveToGroupOperation(4, 1), "1-1.png").IsValid);
        var clash = Plan(folder, new MoveToGroupOperation(2, 1), "1-1.png");

        Assert.False(clash.IsValid);
        Assert.Contains("2-1 (x).png", clash.Rows[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_no_op_operation_is_not_executable()
    {
        using var folder = new TempFolder().With("1-1.png");

        Assert.False(Plan(folder, new ShiftOperation(0, 0), "1-1.png").IsValid);
    }

    [Fact]
    public void Negative_numbers_are_conflicts()
    {
        using var folder = new TempFolder().With("1-1.png");

        Assert.False(Plan(folder, new ShiftOperation(-2, 0), "1-1.png").IsValid);
        Assert.False(Plan(folder, new ShiftOperation(0, -2), "1-1.png").IsValid);
    }

    [Fact]
    public void Close_gaps_renumbers_groups_and_subs()
    {
        using var folder = new TempFolder().With("2-1.png", "2-2 (a).png", "2-5.png", "7.png", "9-3.png");
        var plan = Plan(folder, new CloseGapsOperation(1, true), "2-1.png", "2-2 (a).png", "2-5.png", "7.png", "9-3.png");

        Execute(folder, plan);

        Assert.Equal(["1-1.png", "1-2 (a).png", "1-3.png", "2.png", "3-1.png"], folder.Names());
    }

    [Fact]
    public void Move_to_group_appends_selection_in_order()
    {
        using var folder = new TempFolder().With("3-1.png", "3-2.png", "4 (x).png", "4-1.png");
        var plan = Plan(folder, new MoveToGroupOperation(3, 3), "4 (x).png", "4-1.png");

        Execute(folder, plan);

        Assert.Equal(["3-1.png", "3-2.png", "3-3 (x).png", "3-4.png"], folder.Names());
    }

    [Fact]
    public void Normalise_solos_fixes_lone_members_and_solos_beside_members()
    {
        using var folder = new TempFolder().With("1-1 (only).png", "2 (first).png", "2-2.png", "3 (s).png", "3-1.png");
        var plan = Plan(folder, new NormaliseSolosOperation(), "1-1 (only).png", "2 (first).png", "3 (s).png");

        Execute(folder, plan);

        Assert.Equal(["1 (only).png", "2-1 (first).png", "2-2.png", "3-1 (s).png", "3-2.png"], folder.Names());
    }

    [Fact]
    public void Unchanged_rows_produce_no_steps()
    {
        using var folder = new TempFolder().With("1-1.png");
        var plan = Plan(folder, new CloseGapsOperation(1, true), "1-1.png");

        Assert.Equal(PlanRowStatus.Unchanged, Assert.Single(plan.Rows).Status);
        Assert.False(plan.IsValid);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Random_valid_plans_preserve_every_file_and_tail(int seed)
    {
        var random = new Random(seed);
        for (var round = 0; round < 40; round++)
        {
            using var folder = new TempFolder();
            var names = RandomFolder(random);
            folder.With([.. names]);
            var index = FolderIndex.Scan(folder.Path);
            var selection = index.Shots.Where(_ => random.Next(3) > 0).Select(s => s.FileName).ToList();
            RenumberOperation op = random.Next(4) switch
            {
                0 => new ShiftOperation(random.Next(-3, 4), random.Next(-2, 3)),
                1 => new CloseGapsOperation(random.Next(2) == 0 ? null : random.Next(0, 4), random.Next(2) == 0),
                2 => new MoveToGroupOperation(random.Next(0, 8), random.Next(0, 4)),
                _ => new NormaliseSolosOperation(),
            };
            var plan = RenumberPlanner.Plan(index, selection, op);
            if (!plan.IsValid)
            {
                continue;
            }

            var before = folder.Names().ToDictionary(n => n, folder.Read);
            Execute(folder, plan);
            var after = folder.Names();

            Assert.Equal(before.Count, after.Length);
            var targets = plan.Rows.ToDictionary(r => r.Source, r => r.Target);
            foreach (var (name, content) in before)
            {
                var expected = targets.GetValueOrDefault(name, name);
                Assert.Equal(content, folder.Read(expected));
                Assert.Equal(ShotName(name).Tail, ShotName(expected).Tail);
            }
        }
    }

    private static Naming.ShotName ShotName(string fileName) => Naming.ShotName.TryParse(fileName)!;

    private static List<string> RandomFolder(Random random)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tails = new[] { "", " (a)", " (2026-09-27 10.05.33 UTC+13)", "(x)", " (a) (b)" };
        for (var i = 0; i < random.Next(1, 12); i++)
        {
            var main = random.Next(0, 6);
            var numbering = random.Next(4) == 0 ? $"{main}" : $"{main}-{random.Next(1, 5)}";
            if (!names.Any(n => Naming.ShotName.TryParse(n)!.Numbering == numbering))
            {
                names.Add(numbering + tails[random.Next(tails.Length)] + ".png");
            }
        }

        return [.. names];
    }

    private static RenumberPlan Plan(TempFolder folder, RenumberOperation op, params string[] selection) =>
        RenumberPlanner.Plan(FolderIndex.Scan(folder.Path), selection, op);

    private static void Execute(TempFolder folder, RenumberPlan plan)
    {
        using var state = new TempFolder();
        var result = new RenumberExecutor(state.Path, AppLog.Null).Execute(plan);
        Assert.True(result.Succeeded, result.Error);
        Assert.DoesNotContain(folder.Names(), n => n.EndsWith(".tmp", StringComparison.Ordinal));
    }
}

public class RenumberExecutorTests
{
    [Fact]
    public void Undo_last_reverses_a_completed_renumber()
    {
        using var folder = new TempFolder().With("1-1 (a).png", "1-2.png");
        using var state = new TempFolder();
        var executor = new RenumberExecutor(state.Path, AppLog.Null);
        var plan = RenumberPlanner.Plan(FolderIndex.Scan(folder.Path), ["1-1 (a).png", "1-2.png"], new ShiftOperation(4, 0));
        Assert.True(executor.Execute(plan).Succeeded);
        Assert.Equal(["5-1 (a).png", "5-2.png"], folder.Names());

        Assert.True(executor.CanUndoLast);
        Assert.True(executor.UndoLast().Succeeded);

        Assert.Equal(["1-1 (a).png", "1-2.png"], folder.Names());
        Assert.False(executor.CanUndoLast);
    }

    [Fact]
    public void Crash_mid_run_can_be_resumed()
    {
        using var folder = new TempFolder().With("1.png", "2.png", "3.png");
        using var state = new TempFolder();
        var journal = SimulateCrash(folder, state, completedSteps: 1, stepInDoubtDone: true);
        var executor = new RenumberExecutor(state.Path, AppLog.Null);

        var incomplete = Assert.Single(executor.FindIncomplete());
        Assert.True(executor.Resume(incomplete).Succeeded);

        Assert.Equal(["2.png", "3.png", "4.png"], folder.Names());
        Assert.Empty(executor.FindIncomplete());
        Assert.Equal(3, journal.Steps.Count);
    }

    [Fact]
    public void Crash_mid_run_can_be_rolled_back()
    {
        using var folder = new TempFolder().With("1.png", "2.png", "3.png");
        using var state = new TempFolder();
        SimulateCrash(folder, state, completedSteps: 1, stepInDoubtDone: false);
        var executor = new RenumberExecutor(state.Path, AppLog.Null);

        Assert.True(executor.RollBack(Assert.Single(executor.FindIncomplete())).Succeeded);

        Assert.Equal(["1.png", "2.png", "3.png"], folder.Names());
        Assert.Equal("3.png", folder.Read("3.png"));
        Assert.Empty(executor.FindIncomplete());
    }

    [Fact]
    public void Failure_mid_run_rolls_back_automatically()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows blocks renaming an open file.");
        using var folder = new TempFolder().With("1.png", "2.png", "3.png");
        using var state = new TempFolder();
        var plan = RenumberPlanner.Plan(FolderIndex.Scan(folder.Path), ["1.png", "2.png", "3.png"], new ShiftOperation(1, 0));

        ExecutionResult result;
        using (new FileStream(folder.File("1.png"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = new RenumberExecutor(state.Path, AppLog.Null).Execute(plan);
        }

        Assert.False(result.Succeeded);
        Assert.Equal(["1.png", "2.png", "3.png"], folder.Names());
        Assert.Equal("1.png", folder.Read("1.png"));
    }

    [Fact]
    public void Malformed_journal_escaping_the_folder_is_ignored()
    {
        using var state = new TempFolder();
        var evil = new RenameJournal("x", Path.GetTempPath(), "evil", DateTimeOffset.UtcNow, [new RenameStep("..\\..\\a.png", "b.png")]);
        File.WriteAllText(Path.Combine(state.Path, "x.plan.json"), JsonSerializer.Serialize(evil, JournalJsonContext.Default.RenameJournal));

        Assert.Empty(new RenumberExecutor(state.Path, AppLog.Null).FindIncomplete());
    }

    /// <summary>Writes the journal a shift of +1 over 1,2,3 would, then performs only some renames, as if the process died.</summary>
    private static RenameJournal SimulateCrash(TempFolder folder, TempFolder state, int completedSteps, bool stepInDoubtDone)
    {
        var plan = RenumberPlanner.Plan(FolderIndex.Scan(folder.Path), ["1.png", "2.png", "3.png"], new ShiftOperation(1, 0));
        var journal = new RenameJournal("crash", folder.Path, plan.Description, DateTimeOffset.UtcNow, plan.Steps);
        File.WriteAllText(Path.Combine(state.Path, "crash.plan.json"), JsonSerializer.Serialize(journal, JournalJsonContext.Default.RenameJournal));
        var progress = new List<string>();
        for (var i = 0; i < completedSteps; i++)
        {
            File.Move(folder.File(plan.Steps[i].From), folder.File(plan.Steps[i].To));
            progress.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (stepInDoubtDone)
        {
            var step = plan.Steps[completedSteps];
            File.Move(folder.File(step.From), folder.File(step.To));
        }

        File.WriteAllText(Path.Combine(state.Path, "crash.progress"), string.Join('\n', progress) + "\n");
        return journal;
    }
}
