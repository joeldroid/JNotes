namespace JNotes.Web.Components.Notes;

/// <summary>
/// Cancel-and-restart debounce: each <see cref="Trigger"/> call cancels any delay already in
/// progress and starts a new one, so only the call that goes uninterrupted for the full
/// <paramref name="delay"/> actually invokes <paramref name="onFire"/> — exactly what coalesces a
/// burst of keystrokes into one eventual autosave. <see cref="Trigger"/> returns immediately
/// (the delay and <paramref name="onFire"/> run as a detached task), so it's safe to call directly
/// from a UI event handler without blocking further input.
/// </summary>
internal sealed class AutosaveDebouncer(TimeSpan delay, Func<Task> onFire) : IDisposable
{
    private CancellationTokenSource? _cts;

    public void Trigger()
    {
        Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = RunAsync(cts.Token);
    }

    /// <summary>Cancels a pending delay without firing — used when the note is about to be deleted, or a caller is about to flush explicitly instead.</summary>
    public void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Cancel();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer edit, or explicitly cancelled — nothing to do
        }

        await onFire();
    }
}
