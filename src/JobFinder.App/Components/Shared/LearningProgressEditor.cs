using System.Text.Json;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Preparation;

namespace JobFinder.App.Components.Shared;

public sealed record LearningEdit(LearningItemSnapshot? Item, LearningProgressSnapshot? Reset, string? State, bool? Approved);

public sealed class LearningProgressEditor(InterviewProgress progress, string scope)
{
    public LearningProgressSnapshot? Snapshot { get; private set; }
    public LearningEdit? Draft { get; private set; }
    public bool Conflict { get; private set; }
    public bool Busy { get; private set; }
    public string? Message { get; private set; }

    public async Task LoadAsync(CancellationToken ct)
    {
        if (Snapshot is null) Snapshot = await progress.CaptureAsync(scope, ct);
    }

    public async Task ReloadAsync(CancellationToken ct)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var snapshot = await progress.CaptureAsync(scope, ct);
            Snapshot = snapshot;
            if (Draft is { } draft) Draft = draft with { Item = draft.Item is null ? null : snapshot.Item(draft.Item.Key), Reset = draft.Reset is null ? null : Copy(snapshot) };
            Conflict = false;
            Message = Draft is null ? "Состояние обновлено." : "Состояние обновлено; желаемое изменение сохранено. Примените его отдельно.";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Message = error.Message;
            Conflict = true;
        }
        finally { Busy = false; }
    }

    public Task<bool> MarkAsync(string key, string state, CancellationToken ct) =>
        NewAsync(new(Snapshot?.Item(key), null, state, null), ct);
    public Task<bool> ApproveAsync(string key, bool approved, CancellationToken ct) =>
        NewAsync(new(Snapshot?.Item(key), null, null, approved), ct);
    public Task<bool> ResetAsync(CancellationToken ct) => NewAsync(new(null, Snapshot is null ? null : Copy(Snapshot), null, null), ct);

    private Task<bool> NewAsync(LearningEdit draft, CancellationToken ct)
    {
        if (Snapshot is null || Busy || Conflict) return Task.FromResult(false);
        Draft = draft;
        return RetryAsync(ct);
    }

    public async Task<bool> RetryAsync(CancellationToken ct)
    {
        if (Busy || Conflict || Draft is not { } draft) return false;
        Busy = true;
        try
        {
            var result = draft.Reset is not null ? await progress.ResetAsync(draft.Reset, ct)
                : draft.State is not null ? await progress.MarkAsync(draft.Item!, draft.State, ct)
                : await progress.ApproveAsync(draft.Item!, draft.Approved!.Value, ct);
            Message = result.Result.Message;
            if (!result.Result.Ok) { Conflict = true; return false; }
            Snapshot = result.Snapshot;
            Draft = null;
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Message = error.Message;
            Conflict = true;
            return false;
        }
        finally { Busy = false; }
    }

    private static LearningProgressSnapshot Copy(LearningProgressSnapshot snapshot) => JsonSerializer.Deserialize(
        JsonSerializer.Serialize(snapshot, LearningProgressJsonContext.Default.LearningProgressSnapshot), LearningProgressJsonContext.Default.LearningProgressSnapshot)!;
}
