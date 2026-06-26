using System.Collections.Generic;
using UnityEngine;

namespace SDFImageKit
{
    /// <summary>
    /// Process-wide cache of runtime-generated <see cref="SDFData"/>, keyed by source texture and
    /// generation parameters. Ensures that many <see cref="SDFImage"/> instances sharing a sprite
    /// sheet or atlas only pay for one bake. Entries are reference-counted so they can be released
    /// when no Image needs them anymore.
    /// </summary>
    public static class SDFRuntimeCache
    {
        // Keyed on the texture REFERENCE (identity), not its instance id. This avoids
        // Object.GetInstanceID() (error-level obsolete in newer Unity, no portable replacement
        // across versions) and works identically on every Unity version.
        private readonly struct Key : System.IEquatable<Key>
        {
            public readonly Texture texture;
            public readonly int paramHash;
            public Key(Texture texture, int paramHash) { this.texture = texture; this.paramHash = paramHash; }
            public bool Equals(Key o) => ReferenceEquals(texture, o.texture) && paramHash == o.paramHash;
            public override bool Equals(object obj) => obj is Key k && Equals(k);
            public override int GetHashCode() =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(texture) * 397 ^ paramHash;
        }

        private class Entry { public SDFData data; public int refCount; }

        private static readonly Dictionary<Key, Entry> s_Cache = new Dictionary<Key, Entry>();

        /// <summary>
        /// Get (or bake on first request) the field for a source texture. Increments the ref count;
        /// pair every call with <see cref="Release"/>.
        /// </summary>
        public static SDFData Acquire(Texture source, SDFGenerationParams p)
        {
            if (source == null) return null;
            var key = new Key(source, p.GetHashCode());

            if (s_Cache.TryGetValue(key, out var entry) && entry.data != null && entry.data.IsValid)
            {
                entry.refCount++;
                return entry.data;
            }

            var data = SDFGenerator.GenerateData(source, p);
            if (data == null) return null;
            s_Cache[key] = new Entry { data = data, refCount = 1 };
            return data;
        }

        /// <summary>Release a previously acquired field; the bake is freed when no one holds it.</summary>
        public static void Release(Texture source, SDFGenerationParams p)
        {
            if (source == null) return;
            var key = new Key(source, p.GetHashCode());
            if (!s_Cache.TryGetValue(key, out var entry)) return;

            if (--entry.refCount <= 0)
            {
                if (entry.data != null) entry.data.DisposeRuntime();
                s_Cache.Remove(key);
            }
        }

        /// <summary>Drop every cached field. Call on scene teardown if you want a clean slate.</summary>
        public static void Clear()
        {
            foreach (var kv in s_Cache)
                if (kv.Value.data != null) kv.Value.data.DisposeRuntime();
            s_Cache.Clear();
        }
    }
}
