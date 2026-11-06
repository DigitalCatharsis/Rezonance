// REZONANCE VN ENGINE - staging overlay and speaker map assets.
// These are the hand-authored layer: the markdown stays the text source of truth, this carries
// "how it looks and sounds". Owner: contract layer. Written by the importer as stubs, edited by humans.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Rezonance.VN.Core
{
    /// <summary>How a cue row matches a node.</summary>
    public enum VNMatchKind
    {
        /// <summary>Exact node id.</summary>
        Node = 0,
        /// <summary>Any node inside this branch id.</summary>
        Branch = 1,
        /// <summary>Any node inside this scene id (lowest priority, used for the location default).</summary>
        Scene = 2
    }

    /// <summary>Audio instruction stored in an overlay row.</summary>
    [Serializable]
    public sealed class VNAudioRow
    {
        public VNAudioBus bus = VNAudioBus.Music;
        public string cueId;
        public float fadeSeconds = 1f;
        public bool loop = true;
        public bool stop;
    }

    /// <summary>One staging rule. Priority: Node &gt; Branch &gt; Scene; first hit wins per field.</summary>
    [Serializable]
    public sealed class VNStageCueRow
    {
        public VNMatchKind match = VNMatchKind.Scene;
        /// <summary>Id to match against (node/branch/scene id).</summary>
        public string matchId;
        public string backdropKey;
        public VNFadeKind fade = VNFadeKind.CrossFade;
        public float fadeSeconds = 0.6f;
        public bool fadeSpecified;
        public List<PortraitRef> portraits = new List<PortraitRef>();
        public bool hideAllPortraits;
        public List<VNAudioRow> audio = new List<VNAudioRow>();
        public string screenText;
        public float holdSeconds;
        public float typingSpeed;
        public string miniGameId;
        public string note;
    }

    /// <summary>
    /// Staging overlay. Field-level merge: a Branch row supplies only the fields it actually sets,
    /// a Scene row fills the rest. That keeps per-scene defaults cheap and per-node overrides precise.
    /// </summary>
    [CreateAssetMenu(menuName = "Rezonance/VN Stage Overlay", fileName = "VNStageOverlay")]
    public sealed class VNStageOverlay : ScriptableObject, IStageOverlay
    {
        [SerializeField] private List<VNStageCueRow> rows = new List<VNStageCueRow>();

        public IReadOnlyList<VNStageCueRow> Rows => rows;

        private readonly Dictionary<string, VNStageCueRow> byNode = new Dictionary<string, VNStageCueRow>();
        private readonly Dictionary<string, VNStageCueRow> byBranch = new Dictionary<string, VNStageCueRow>();
        private readonly Dictionary<string, VNStageCueRow> byScene = new Dictionary<string, VNStageCueRow>();
        private bool indexed;

        public void Reindex()
        {
            byNode.Clear(); byBranch.Clear(); byScene.Clear();
            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrEmpty(row.matchId)) continue;
                switch (row.match)
                {
                    case VNMatchKind.Node: byNode[row.matchId] = row; break;
                    case VNMatchKind.Branch: byBranch[row.matchId] = row; break;
                    case VNMatchKind.Scene: byScene[row.matchId] = row; break;
                }
            }
            indexed = true;
        }

        public VNStageCues ForNode(VNNode node)
        {
            if (node == null) return VNStageCues.Empty;
            if (!indexed) Reindex();

            byScene.TryGetValue(new VNNodeId(node.id).SceneId, out var sceneRow);
            byBranch.TryGetValue(new VNNodeId(node.id).BranchId, out var branchRow);
            byNode.TryGetValue(node.id, out var nodeRow);

            if (sceneRow == null && branchRow == null && nodeRow == null) return VNStageCues.Empty;

            var cues = new VNStageCues();
            Merge(cues, sceneRow);
            Merge(cues, branchRow);
            Merge(cues, nodeRow);
            return cues;
        }

        public bool HasCues(VNNode node)
        {
            if (node == null) return false;
            if (!indexed) Reindex();
            var parsed = new VNNodeId(node.id);
            return byNode.ContainsKey(node.id) || byBranch.ContainsKey(parsed.BranchId) || byScene.ContainsKey(parsed.SceneId);
        }

        /// <summary>Adds or replaces a row. Used by the importer to seed stubs without clobbering edits.</summary>
        public void UpsertRow(VNStageCueRow row)
        {
            if (row == null || string.IsNullOrEmpty(row.matchId)) return;
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null && rows[i].match == row.match && rows[i].matchId == row.matchId)
                {
                    rows[i] = row;
                    indexed = false;
                    return;
                }
            }
            rows.Add(row);
            indexed = false;
        }

        private static void Merge(VNStageCues cues, VNStageCueRow row)
        {
            if (row == null) return;
            if (!string.IsNullOrEmpty(row.backdropKey)) cues.backdropKey = row.backdropKey;
            if (row.fadeSpecified)
            {
                cues.fade = row.fade;
                cues.fadeSeconds = row.fadeSeconds;
            }
            if (row.portraits != null && row.portraits.Count > 0) cues.portraits = row.portraits.ToArray();
            if (row.hideAllPortraits) cues.hideAllPortraits = true;
            if (row.audio != null && row.audio.Count > 0)
            {
                var list = new List<AudioCue>(cues.audio);
                foreach (var a in row.audio)
                {
                    if (a == null || string.IsNullOrEmpty(a.cueId)) continue;
                    list.Add(new AudioCue(a.bus, a.cueId, a.fadeSeconds, a.stop));
                }
                cues.audio = list.ToArray();
            }
            if (!string.IsNullOrEmpty(row.screenText)) cues.screenText = row.screenText;
            if (row.holdSeconds > 0f) cues.holdSeconds = row.holdSeconds;
            if (row.typingSpeed > 0f) cues.typingSpeed = row.typingSpeed;
            if (!string.IsNullOrEmpty(row.miniGameId)) cues.miniGameId = row.miniGameId;
        }
    }

    /// <summary>Row of the speaker map: raw source label to engine identity.</summary>
    [Serializable]
    public sealed class VNSpeakerRow
    {
        /// <summary>Label exactly as in the markdown, e.g. "IRENA".</summary>
        public string label;
        /// <summary>Stable ascii id, e.g. "irena". Empty for ambient voices without portraits.</summary>
        public string characterId;
        public VNSpeakerRole role = VNSpeakerRole.Ambient;
        public PortraitSlot defaultSlot = PortraitSlot.None;
        public string defaultEmotion = "neutral";
        /// <summary>Localization key for the name plate; defaults to "name." + label slug when empty.</summary>
        public string nameKey;
        public int lineCount;
    }

    /// <summary>Maps script speaker labels to characters, roles and portrait slots.</summary>
    [CreateAssetMenu(menuName = "Rezonance/VN Speaker Map", fileName = "VNSpeakerMap")]
    public sealed class VNSpeakerMap : ScriptableObject
    {
        [SerializeField] private List<VNSpeakerRow> rows = new List<VNSpeakerRow>();

        public IReadOnlyList<VNSpeakerRow> Rows => rows;

        private Dictionary<string, VNSpeakerRow> byLabel;

        public void Reindex()
        {
            byLabel = new Dictionary<string, VNSpeakerRow>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                if (r != null && !string.IsNullOrEmpty(r.label)) byLabel[r.label] = r;
            }
        }

        public bool TryGet(string label, out VNSpeakerRow row)
        {
            if (byLabel == null) Reindex();
            row = null;
            return label != null && byLabel.TryGetValue(label, out row);
        }

        /// <summary>Role for a label, falling back to Ambient for unknown one-off voices.</summary>
        public VNSpeakerRole RoleOf(string label)
        {
            return TryGet(label, out var row) ? row.role : VNSpeakerRole.Ambient;
        }

        public string NameKeyFor(string label)
        {
            if (TryGet(label, out var row) && !string.IsNullOrEmpty(row.nameKey)) return row.nameKey;
            return "name." + VNSlug.Of(label ?? string.Empty);
        }

        public PortraitRef? PortraitFor(string label, string emotion = null)
        {
            if (!TryGet(label, out var row)) return null;
            if (string.IsNullOrEmpty(row.characterId) || row.defaultSlot == PortraitSlot.None) return null;
            var emo = string.IsNullOrEmpty(emotion) ? row.defaultEmotion : emotion;
            return new PortraitRef(row.characterId, emo, row.defaultSlot);
        }

        public void UpsertRow(VNSpeakerRow row)
        {
            if (row == null || string.IsNullOrEmpty(row.label)) return;
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null && rows[i].label == row.label) { rows[i] = row; return; }
            }
            rows.Add(row);
            byLabel = null;
        }
    }
}
