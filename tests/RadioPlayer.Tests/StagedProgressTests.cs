using RadioPlayer.ViewModels;
using Xunit;

namespace RadioPlayer.Tests;

public class StagedProgressTests
{
    private static readonly string[] SearchStages = ["Directory", "Rank", "Validate"];

    private static string[] Labels(StagedProgress p) => p.Stages.Select(s => s.Label).ToArray();
    private static StageStatus[] Statuses(StagedProgress p) => p.Stages.Select(s => s.Status).ToArray();

    [Fact]
    public void Begin_DeclaresAllStagesAndStartsTheFirst()
    {
        var progress = new StagedProgress();

        progress.Begin(SearchStages);

        Assert.Equal(SearchStages, Labels(progress));
        Assert.Equal([StageStatus.Active, StageStatus.Pending, StageStatus.Pending], Statuses(progress));
    }

    [Fact]
    public void Step_CompletesEverythingBeforeTheNamedStage()
    {
        var progress = new StagedProgress();
        progress.Begin(SearchStages);

        progress.Step("Validate");

        Assert.Equal([StageStatus.Done, StageStatus.Done, StageStatus.Active], Statuses(progress));
    }

    /// <summary>The search pipeline only escalates to the web on some runs, so that stage isn't
    /// declared up front — it has to appear in the right place when it does happen.</summary>
    [Fact]
    public void Step_InsertsAnUndeclaredStageAfterTheRunningOne()
    {
        var progress = new StagedProgress();
        progress.Begin(SearchStages);
        progress.Step("Rank");

        progress.Step("Web");

        Assert.Equal(["Directory", "Rank", "Web", "Validate"], Labels(progress));
        Assert.Equal([StageStatus.Done, StageStatus.Done, StageStatus.Active, StageStatus.Pending],
            Statuses(progress));
    }

    [Fact]
    public void Step_OnARunWithoutTheOptionalStage_LeavesItOut()
    {
        var progress = new StagedProgress();
        progress.Begin(SearchStages);

        progress.Step("Rank");
        progress.Step("Validate");

        Assert.Equal(SearchStages, Labels(progress));
        Assert.Equal([StageStatus.Done, StageStatus.Done, StageStatus.Active], Statuses(progress));
    }

    [Fact]
    public void Step_IsIdempotentForTheAlreadyRunningStage()
    {
        var progress = new StagedProgress();
        progress.Begin(SearchStages);

        progress.Step("Directory");

        Assert.Equal(SearchStages, Labels(progress));
        Assert.Equal([StageStatus.Active, StageStatus.Pending, StageStatus.Pending], Statuses(progress));
    }

    [Fact]
    public void Finish_MarksEverythingDone()
    {
        var progress = new StagedProgress();
        progress.Begin(SearchStages);

        progress.Finish();

        Assert.All(progress.Stages, s => Assert.Equal(StageStatus.Done, s.Status));
    }

    [Fact]
    public void Reset_ClearsTheChecklistForTheNextRun()
    {
        var progress = new StagedProgress();
        progress.Begin(SearchStages);

        progress.Reset();

        Assert.Empty(progress.Stages);
    }

    [Fact]
    public void CanCancel_FollowsWhetherACommandWasSupplied()
    {
        Assert.False(new StagedProgress().CanCancel);
        Assert.True(new StagedProgress
        {
            CancelCommand = new Mvvm.RelayCommand(() => { })
        }.CanCancel);
    }
}
