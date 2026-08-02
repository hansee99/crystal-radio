using System.Collections.ObjectModel;
using System.Windows.Input;
using RadioPlayer.Mvvm;

namespace RadioPlayer.ViewModels;

/// <summary>Where one step of a multi-step operation has got to.</summary>
public enum StageStatus
{
    /// <summary>Declared up front but not started — shown so the user can see what's coming.</summary>
    Pending,

    /// <summary>Running right now (exactly one stage at a time).</summary>
    Active,

    /// <summary>Finished; stays on screen ticked, so progress accumulates visibly.</summary>
    Done
}

/// <summary>One named step in a <see cref="StagedProgress"/>.</summary>
public sealed class ProgressStage : ObservableObject
{
    private StageStatus _status;

    public ProgressStage(string label, StageStatus status = StageStatus.Pending)
    {
        Label = label;
        _status = status;
    }

    public string Label { get; }

    public StageStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }
}

/// <summary>
/// Progress for a long AI operation, as named steps that tick off one by one (UX audit:
/// rotating status copy alone doesn't signal progress — after twenty seconds the user can't
/// tell "working" from "hung"). Completed steps stay on screen so the wait visibly accumulates.
///
/// Steps don't all have to be known up front: <see cref="Step"/> inserts a label it hasn't seen
/// after the running one, which is how the search pipeline's conditional web escalation shows up
/// only on the runs where it actually happens.
/// </summary>
public sealed class StagedProgress : ObservableObject
{
    /// <summary>Steps in running order — the checklist the panel renders.</summary>
    public ObservableCollection<ProgressStage> Stages { get; } = new();

    /// <summary>Abandons the operation. Null when it isn't cancellable, which hides the
    /// affordance. The audit asks for this on anything expected to exceed ~5 seconds.</summary>
    public ICommand? CancelCommand { get; init; }

    public bool CanCancel => CancelCommand is not null;

    /// <summary>Declares the steps that always run, all pending, and starts the first one.</summary>
    public void Begin(params string[] labels)
    {
        Stages.Clear();
        foreach (var label in labels)
            Stages.Add(new ProgressStage(label));
        if (Stages.Count > 0)
            Stages[0].Status = StageStatus.Active;
    }

    /// <summary>
    /// Makes <paramref name="label"/> the running step, marking everything before it done. A
    /// label that wasn't declared by <see cref="Begin"/> is inserted directly after the running
    /// step — so a conditional stage appears in the right place, only on the runs it happens.
    /// </summary>
    public void Step(string label)
    {
        var index = IndexOf(label);
        if (index < 0)
        {
            index = Math.Clamp(ActiveIndex + 1, 0, Stages.Count);
            Stages.Insert(index, new ProgressStage(label));
        }

        for (var i = 0; i < index; i++)
            Stages[i].Status = StageStatus.Done;
        Stages[index].Status = StageStatus.Active;
    }

    /// <summary>Marks every step done (the operation finished).</summary>
    public void Finish()
    {
        foreach (var stage in Stages)
            stage.Status = StageStatus.Done;
    }

    public void Reset() => Stages.Clear();

    private int ActiveIndex
    {
        get
        {
            for (var i = 0; i < Stages.Count; i++)
                if (Stages[i].Status == StageStatus.Active)
                    return i;
            return -1;
        }
    }

    private int IndexOf(string label)
    {
        for (var i = 0; i < Stages.Count; i++)
            if (string.Equals(Stages[i].Label, label, StringComparison.Ordinal))
                return i;
        return -1;
    }
}
