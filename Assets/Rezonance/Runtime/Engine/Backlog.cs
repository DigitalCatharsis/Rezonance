// REZONANCE VN ENGINE - bounded dialogue history for the log screen.
// Ring buffer sized by VNSettings.backlogMaxEntries; a chronological snapshot is rebuilt lazily so the
// hot path (one Add per shown node) never shifts the array. Only node ids are ever serialized.
using System;
using System.Collections.Generic;
using Rezonance.VN.Core;

namespace Rezonance.VN.Engine
{
    /// <summary>Circular buffer of <see cref="VNBacklogEntry"/>.</summary>
    public sealed class Backlog
    {
        private const int MinCapacity = 16;

        private VNBacklogEntry[] ring;
        private int head;
        private int size;
        private int capacity;
        private List<VNBacklogEntry> snapshot;
        private bool snapshotDirty;

        /// <summary>Creates a buffer holding at most <paramref name="capacity"/> entries.</summary>
        public Backlog(int capacity = 400)
        {
            Resize(Math.Max(MinCapacity, capacity));
        }

        /// <summary>Current maximum number of remembered lines.</summary>
        public int Capacity => capacity;

        /// <summary>Number of stored entries.</summary>
        public int Count => size;

        /// <summary>True when nothing was recorded yet.</summary>
        public bool IsEmpty => size == 0;

        /// <summary>Oldest first; the view may iterate it while the runtime keeps appending.</summary>
        public IReadOnlyList<VNBacklogEntry> Entries
        {
            get
            {
                if (snapshotDirty || snapshot == null) RebuildSnapshot();
                return snapshot;
            }
        }

        /// <summary>Appends a line and drops the oldest when the buffer is full. Null entries are ignored.</summary>
        public void Add(VNBacklogEntry entry)
        {
            if (entry == null) return;
            if (size == capacity)
            {
                ring[head] = entry;
                head = (head + 1) % capacity;
            }
            else
            {
                ring[(head + size) % capacity] = entry;
                size++;
            }
            snapshotDirty = true;
        }

        /// <summary>Convenience overload used by the runtime.</summary>
        public void Add(VNNode node, string speakerName, string text)
        {
            if (node == null) return;
            Add(new VNBacklogEntry
            {
                nodeId = node.id,
                speakerName = speakerName,
                text = text,
                kind = node.kind
            });
        }

        /// <summary>Node ids oldest first, ready for VNGameState.backlogNodeIds.</summary>
        public List<string> NodeIds()
        {
            var list = new List<string>(size);
            for (var i = 0; i < size; i++)
            {
                var e = ring[(head + i) % capacity];
                list.Add(e != null ? e.nodeId : null);
            }
            return list;
        }

        /// <summary>Last stored node id, or null when empty.</summary>
        public string LastNodeId
        {
            get
            {
                if (size == 0) return null;
                var e = ring[(head + size - 1) % capacity];
                return e != null ? e.nodeId : null;
            }
        }

        /// <summary>True when the id is currently remembered (used by Skip to avoid double logging).</summary>
        public bool ContainsNode(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return false;
            for (var i = 0; i < size; i++)
            {
                var e = ring[(head + i) % capacity];
                if (e != null && string.Equals(e.nodeId, nodeId, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Forgets everything (new game).</summary>
        public void Clear()
        {
            Array.Clear(ring, 0, ring.Length);
            head = 0;
            size = 0;
            snapshotDirty = true;
        }

        /// <summary>Changes the bound, keeping the newest entries.</summary>
        public void Resize(int newCapacity)
        {
            newCapacity = Math.Max(MinCapacity, newCapacity);
            if (newCapacity == capacity && ring != null) return;
            var keep = Entries;
            var next = new VNBacklogEntry[newCapacity];
            var start = Math.Max(0, keep.Count - newCapacity);
            var n = keep.Count - start;
            for (var i = 0; i < n; i++) next[i] = keep[start + i];
            ring = next;
            capacity = newCapacity;
            head = 0;
            size = n;
            snapshotDirty = true;
        }

        /// <summary>
        /// Rebuilds the history after a load. Entries older than the save are lost by design, so the caller
        /// supplies a factory that resolves a node id into the row to display. Returns the number restored.
        /// </summary>
        public int Restore(IList<string> nodeIds, Func<string, VNBacklogEntry> factory)
        {
            Clear();
            if (nodeIds == null || factory == null) return 0;
            var restored = 0;
            for (var i = 0; i < nodeIds.Count; i++)
            {
                var id = nodeIds[i];
                if (string.IsNullOrEmpty(id)) continue;
                var entry = factory(id);
                if (entry == null) continue;
                Add(entry);
                restored++;
            }
            return restored;
        }

        private void RebuildSnapshot()
        {
            if (snapshot == null) snapshot = new List<VNBacklogEntry>(capacity);
            else snapshot.Clear();
            for (var i = 0; i < size; i++)
            {
                var e = ring[(head + i) % capacity];
                if (e != null) snapshot.Add(e);
            }
            snapshotDirty = false;
        }
    }
}
