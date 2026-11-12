using System;
using System.Collections.Generic;

namespace Lifecycle.Core
{
    public enum SceneScopeState
    {
        Active,
        Closing,
        Closed
    }

    /// <summary>
    /// One scene's lifecycle authority: pools, leases, and pending work are all scoped to it.
    /// A closed scope refuses new work and holds no references the engine can still mutate.
    /// </summary>
    public sealed class SceneScope
    {
        private readonly Dictionary<string, ObjectPool<PoolSlot>> _pools =
            new Dictionary<string, ObjectPool<PoolSlot>>(StringComparer.Ordinal);
        private readonly HashSet<long> _leaseIds = new HashSet<long>();

        internal SceneScope(string scopeId, string sceneName, ClosePolicy policy)
        {
            ScopeId = scopeId;
            SceneName = sceneName;
            Policy = policy;
            State = SceneScopeState.Active;
        }

        public string ScopeId { get; }
        public string SceneName { get; }
        public ClosePolicy Policy { get; }
        public SceneScopeState State { get; internal set; }
        public bool IsActive => State == SceneScopeState.Active;
        public int ActiveLeaseCount => _leaseIds.Count;

        internal HashSet<long> LeaseIds => _leaseIds;

        internal ObjectPool<PoolSlot> GetPool(string poolKey)
        {
            return _pools.TryGetValue(poolKey, out var pool) ? pool : null;
        }

        internal void AddPool(string poolKey, ObjectPool<PoolSlot> pool) => _pools[poolKey] = pool;

        internal IEnumerable<KeyValuePair<string, ObjectPool<PoolSlot>>> Pools => _pools;

        internal void MarkClosing() => State = SceneScopeState.Closing;

        internal void MarkClosed()
        {
            State = SceneScopeState.Closed;
            _leaseIds.Clear();
            _pools.Clear();
        }

        public override string ToString() => "Scope(" + SceneName + "#" + ScopeId + ", " + State + ", leases=" + _leaseIds.Count + ")";
    }
}
