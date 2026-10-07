namespace JobFinder.Pipeline;

public sealed class ServerOwnership
{
    private int _owned;

    public bool IsOwned => Volatile.Read(ref _owned) == 1;

    internal void Acquired() => Interlocked.Exchange(ref _owned, 1);

    internal void Lost() => Interlocked.Exchange(ref _owned, 0);

    public void EnsureOwned()
    {
        if (!IsOwned) throw new InvalidOperationException("server database ownership is not held");
    }
}
