using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Pipeline.Messaging;

public sealed class NativePublicationScopes(IServiceScopeFactory scopes)
{
    private IServiceScopeFactory? _bootstrap;

    public AsyncServiceScope CreateAsyncScope() => (Volatile.Read(ref _bootstrap) ?? scopes).CreateAsyncScope();

    internal void Begin(IServiceScopeFactory producer)
    {
        if (Interlocked.CompareExchange(ref _bootstrap, producer, null) is not null)
            throw new InvalidOperationException("a bootstrap publisher is already active");
    }

    internal void End(IServiceScopeFactory producer)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _bootstrap, null, producer), producer))
            throw new InvalidOperationException("bootstrap publisher ownership changed");
    }
}
