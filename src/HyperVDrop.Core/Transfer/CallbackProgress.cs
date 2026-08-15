namespace HyperVDrop.Core.Transfer;

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes its callback synchronously on the reporting thread.
/// </summary>
/// <remarks>
/// <see cref="Progress{T}"/> posts to a synchronization context, which reorders reports and hides
/// them behind the UI queue. Progress here flows through the queue's own locking and is marshalled
/// to the UI exactly once, at the edge.
/// </remarks>
internal sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
