#region

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A view model's <c>post</c> delegate that queues what it is given for the test to run on its own thread,
///     and signals each post so the test awaits the hand-back instead of polling for it.
/// </summary>
internal sealed class PostedActions
{
    private const string Posted = "something to be posted back to the UI thread";

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly ChangeSignal _signal = new();

    public bool IsEmpty => _queue.IsEmpty;

    public void Enqueue(Action action)
    {
        _queue.Enqueue(action);
        _signal.Raise();
    }

    public bool TryDequeue([MaybeNullWhen(false)] out Action action) => _queue.TryDequeue(out action);

    public void Drain()
    {
        while (_queue.TryDequeue(out Action? action))
        {
            action();
        }
    }

    /// <summary>Returns once something is queued.</summary>
    public Task WaitForPostAsync() => _signal.WaitUntilAsync(() => !_queue.IsEmpty, Posted);

    /// <summary>Runs each post as it arrives until <paramref name="until" /> holds.</summary>
    public async Task DrainUntilAsync(Func<bool> until)
    {
        DateTime deadline = DateTime.UtcNow + ChangeSignal.Backstop;
        Drain();
        while (!until())
        {
            await _signal.WaitUntilAsync(() => !_queue.IsEmpty, Posted, deadline);
            Drain();
        }
    }
}
