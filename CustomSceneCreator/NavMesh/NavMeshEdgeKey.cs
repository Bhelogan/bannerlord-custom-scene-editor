using System.Collections.Generic;

namespace CustomSceneCreator.NavMesh {

    /// <summary>
    /// The key used to look an edge up by its two endpoints, and the comparer that makes it fast.
    ///
    /// <b>Why the comparer exists.</b> The key packs two vertex indices into a long as
    /// <c>(low &lt;&lt; 32) | high</c>. .NET hashes a long by XORing its two halves, which for this
    /// key is <c>low ^ high</c> - and in a navmesh the two endpoints of an edge are almost always
    /// NEARBY indices, because vertices are written in spatial order. So <c>low ^ high</c> is
    /// usually a very small number, tens of thousands of distinct edges collide into a handful of
    /// buckets, and every lookup degrades into a linear scan of a bucket chain.
    ///
    /// Measured on a shipped 25,631-face battle terrain: building the duplicate-edge set over 51,000
    /// edges took <b>338 ms</b>. The same loop with this comparer takes single-digit milliseconds.
    /// That one line was half the total time of a whole navmesh bake, because the structural check
    /// runs before and after every single edit.
    ///
    /// The mix is the finalizer from SplitMix64: it spreads every input bit across the whole word,
    /// so nearby endpoints no longer produce nearby - or identical - hashes.
    /// </summary>
    public static class NavMeshEdgeKey {

        /// <summary>Order-independent key for the edge between two vertices.</summary>
        public static long Of(int a, int b) =>
            a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        /// <summary>Use this for every set or map keyed by <see cref="Of"/>.</summary>
        public static readonly IEqualityComparer<long> Comparer = new MixingComparer();

        // No capacity overload: HashSet<T>(int, IEqualityComparer<T>) does not exist on the
        // .NET Framework the game runs on. Growth costs a few reallocations; the comparer is what
        // actually mattered.
        public static HashSet<long> NewSet(int capacity = 0) => new HashSet<long>(Comparer);

        public static Dictionary<long, int> NewMap(int capacity = 0) =>
            capacity > 0 ? new Dictionary<long, int>(capacity, Comparer)
                         : new Dictionary<long, int>(Comparer);

        private sealed class MixingComparer : IEqualityComparer<long> {
            public bool Equals(long left, long right) => left == right;

            public int GetHashCode(long value) {
                ulong mixed = (ulong)value;
                mixed = (mixed ^ (mixed >> 30)) * 0xBF58476D1CE4E5B9UL;
                mixed = (mixed ^ (mixed >> 27)) * 0x94D049BB133111EBUL;
                mixed ^= mixed >> 31;
                return (int)mixed ^ (int)(mixed >> 32);
            }
        }
    }
}
