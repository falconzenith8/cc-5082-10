using System;
using System.Collections.Generic;

namespace Lifecycle.Core
{
    /// <summary>Per-borrow generation stamped into every lease handed out by a pool.</summary>
    public readonly struct Generation : IEquatable<Generation>, IComparable<Generation>
    {
        public static readonly Generation None = new Generation(0);

        public Generation(int value)
        {
            Value = value;
        }

        public int Value { get; }
        public bool IsValid => Value > 0;

        public Generation Next() => new Generation(Value + 1);

        public bool Equals(Generation other) => Value == other.Value;
        public override bool Equals(object obj) => obj is Generation other && Equals(other);
        public override int GetHashCode() => Value;
        public int CompareTo(Generation other) => Value.CompareTo(other.Value);
        public override string ToString() => "GEN[" + Value + "]";

        public static bool operator ==(Generation a, Generation b) => a.Value == b.Value;
        public static bool operator !=(Generation a, Generation b) => a.Value != b.Value;
        public static bool operator <(Generation a, Generation b) => a.Value < b.Value;
        public static bool operator >(Generation a, Generation b) => a.Value > b.Value;
    }

    /// <summary>
    /// Monotonic tick source. Unity drives it once per frame; headless tests advance it explicitly
    /// so ordering of async completions is deterministic.
    /// </summary>
    public sealed class Clock
    {
        private long _now = 1;

        public long Now => _now;

        public long Advance() => ++_now;

        public void FastForward(long ticks)
        {
            if (ticks < 0) throw new ArgumentOutOfRangeException(nameof(ticks));
            _now += ticks;
        }
    }

    public static class ListExtensions
    {
        public static void RemoveWhere<T>(List<T> list, Func<T, bool> predicate)
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (predicate(list[i])) list.RemoveAt(i);
            }
        }
    }
}
