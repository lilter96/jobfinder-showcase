using System.Collections.Concurrent;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed class ChannelMonitoring
{
    private readonly ConcurrentDictionary<long, byte> _peers = new();
    private readonly object _gate = new();
    private long _revision;
    public long Revision { get { lock (_gate) return _revision; } }
    public bool Contains(long peerId) => _peers.ContainsKey(peerId);
    public void Add(long peerId) { lock (_gate) if (_peers.TryAdd(peerId, 0)) _revision++; }
    public void Remove(long peerId) { lock (_gate) if (_peers.TryRemove(peerId, out _)) _revision++; }
    public void Reload(IEnumerable<long> peerIds, long expectedRevision)
    {
        var current = peerIds.ToHashSet();
        lock (_gate)
        {
            if (_revision != expectedRevision) return;
            foreach (var peerId in current) Add(peerId);
            foreach (var peerId in _peers.Keys.Where(peerId => !current.Contains(peerId))) Remove(peerId);
        }
    }
    public int Count => _peers.Count;
}
