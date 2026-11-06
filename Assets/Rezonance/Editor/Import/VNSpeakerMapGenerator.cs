// REZONANCE VN EDITOR - speaker map seeder.
// Seeds VNSpeakerMap.asset from the labels actually found in the script. Existing rows are never
// rewritten: only lineCount is refreshed in place, so hand-authored slots and ids survive a reimport.
// The known-character table is keyed by ascii slug, not by the Cyrillic label, so a source file
// re-saved without a BOM cannot silently break the identity of a character.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Rezonance.VN.Core;

namespace Rezonance.VN.Import
{
    /// <summary>Result of one seeding pass, consumed by the validation report.</summary>
    public sealed class VNSpeakerSeedResult
    {
        public int NewRows;
        public int RefreshedRows;
        public readonly List<string> NewLabels = new List<string>();
        public readonly List<string> OrphanLabels = new List<string>();
    }

    /// <summary>Builds and refreshes the label -> character / role / slot map.</summary>
    public static class VNSpeakerMapGenerator
    {
        sealed class Known
        {
            public string Slug;
            public string CharacterId;
            public VNSpeakerRole Role;
            public PortraitSlot Slot;

            public Known(string slug, string characterId, VNSpeakerRole role, PortraitSlot slot)
            {
                Slug = slug;
                CharacterId = characterId;
                Role = role;
                Slot = slot;
            }
        }

        /// <summary>
        /// Named cast. The key is the VNSlug of the Cyrillic label (so "liberti" is ЛИБЕРТИ), the id is the
        /// ascii character id of ARCHITECTURE.md section 8, the slot follows the staging decision:
        /// ИРЕНА Left, ГЛОРИЯ Center, АГАТА Right, ЛИБЕРТИ FarLeft, ТЕРЕН FarRight, everyone else None.
        /// </summary>
        static readonly Known[] KnownLabels =
        {
            new Known("irena", "irena", VNSpeakerRole.Character, PortraitSlot.Left),
            new Known("gloria", "gloria", VNSpeakerRole.Character, PortraitSlot.Center),
            new Known("agata", "agata", VNSpeakerRole.Character, PortraitSlot.Right),
            new Known("liberti", "liberty", VNSpeakerRole.Character, PortraitSlot.FarLeft),
            new Known("teren", "teren", VNSpeakerRole.Character, PortraitSlot.FarRight),
            new Known("minerva", "minerva", VNSpeakerRole.Character, PortraitSlot.None),
            new Known("nika", "nika", VNSpeakerRole.Character, PortraitSlot.None),
            new Known("ruta", "ruta", VNSpeakerRole.Character, PortraitSlot.None),
            new Known("barmen", "bartender", VNSpeakerRole.Character, PortraitSlot.None),
            new Known("salvator", "salvator", VNSpeakerRole.Character, PortraitSlot.None),
            new Known("liora", "liora", VNSpeakerRole.Character, PortraitSlot.None),
            new Known("semen", "semen", VNSpeakerRole.Character, PortraitSlot.None),
            new Known("vy", string.Empty, VNSpeakerRole.Player, PortraitSlot.None)
        };

        /// <summary>Machine voices: name plate only, no portrait, system styling.</summary>
        static readonly HashSet<string> SystemSlugs = new HashSet<string>(StringComparer.Ordinal)
        {
            "ekran", "terminal", "ekran-peredatchika", "arhivnaya-stroka",
            "golos-sistemy", "golos-iz-terminala", "golos-sluzhby", "golos-vedushchey",
            "zhenskiy-golos", "zhenskiy-golos-iz-dinamika"
        };

        /// <summary>Slug of the player label ("Vy"), used to keep player roles stable.</summary>
        public const string PlayerSlug = "vy";

        /// <summary>Loads the map asset or creates a fresh one at the given project path.</summary>
        public static VNSpeakerMap LoadOrCreate(string assetPath)
        {
            var map = AssetDatabase.LoadAssetAtPath<VNSpeakerMap>(assetPath);
            if (map != null) return map;
            map = ScriptableObject.CreateInstance<VNSpeakerMap>();
            map.name = "VNSpeakerMap";
            AssetDatabase.CreateAsset(map, assetPath);
            return map;
        }

        /// <summary>Seeds missing rows, refreshes the counters, lists rows the script no longer uses.</summary>
        public static VNSpeakerSeedResult Seed(VNSpeakerMap map, VNParseResult res)
        {
            var result = new VNSpeakerSeedResult();
            if (map == null || res == null) return result;

            for (var i = 0; i < res.SpeakerOrder.Count; i++)
            {
                var label = res.SpeakerOrder[i];
                var count = res.SpeakerCounts[label];
                VNSpeakerRow existing;
                if (map.TryGet(label, out existing))
                {
                    if (existing.lineCount != count) existing.lineCount = count;
                    result.RefreshedRows++;
                    continue;
                }
                var row = BuildRow(label, count);
                map.UpsertRow(row);
                result.NewRows++;
                result.NewLabels.Add(label + " -> " +
                    (string.IsNullOrEmpty(row.characterId) ? "(no id)" : row.characterId) + " / " +
                    row.role + " / " + row.defaultSlot);
            }

            map.Reindex();
            var rows = map.Rows;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null || string.IsNullOrEmpty(row.label)) continue;
                int count;
                if (!res.SpeakerCounts.TryGetValue(row.label, out count))
                {
                    count = 0;
                    result.OrphanLabels.Add(row.label);
                }
                if (row.lineCount != count) row.lineCount = count;
            }

            EditorUtility.SetDirty(map);
            return result;
        }

        /// <summary>Builds a row for a label that is not in the map yet.</summary>
        public static VNSpeakerRow BuildRow(string label, int lineCount)
        {
            var slug = VNSlug.Of(label ?? string.Empty);
            var row = new VNSpeakerRow
            {
                label = label,
                role = VNSpeakerRole.Ambient,
                characterId = string.Empty,
                defaultSlot = PortraitSlot.None,
                defaultEmotion = "neutral",
                nameKey = VNImportNaming.NameKey(label),
                lineCount = lineCount
            };

            for (var i = 0; i < KnownLabels.Length; i++)
            {
                if (KnownLabels[i].Slug != slug) continue;
                row.characterId = KnownLabels[i].CharacterId;
                row.role = KnownLabels[i].Role;
                row.defaultSlot = KnownLabels[i].Slot;
                return row;
            }

            if (SystemSlugs.Contains(slug)) row.role = VNSpeakerRole.System;
            return row;
        }

        /// <summary>
        /// Stamps the role of every node from the map, keeping inner-voice nodes as InnerVoice and player
        /// lines as Player. Run after seeding so the JSON carries the resolved roles.
        /// </summary>
        public static int ApplyRolesToNodes(VNScriptData data, VNSpeakerMap map)
        {
            if (data == null || map == null || data.scenes == null) return 0;
            var stamped = 0;
            for (var s = 0; s < data.scenes.Count; s++)
            {
                var scene = data.scenes[s];
                if (scene == null || scene.branches == null) continue;
                for (var b = 0; b < scene.branches.Count; b++)
                {
                    var branch = scene.branches[b];
                    if (branch == null || branch.nodes == null) continue;
                    for (var n = 0; n < branch.nodes.Count; n++)
                    {
                        var node = branch.nodes[n];
                        if (node == null || string.IsNullOrEmpty(node.speaker)) continue;
                        if (node.kind == VNNodeKind.InnerVoice)
                        {
                            node.role = VNSpeakerRole.InnerVoice;
                            stamped++;
                            continue;
                        }
                        if (VNSlug.Of(node.speaker) == PlayerSlug) node.role = VNSpeakerRole.Player;
                        else node.role = map.RoleOf(node.speaker);
                        stamped++;
                    }
                }
            }
            return stamped;
        }

        /// <summary>Labels that carry a portrait slot - used by the report and the placeholder tool.</summary>
        public static List<string> LabelsWithPortrait(VNSpeakerMap map)
        {
            var list = new List<string>();
            if (map == null) return list;
            var rows = map.Rows;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null || string.IsNullOrEmpty(row.characterId)) continue;
                if (row.defaultSlot == PortraitSlot.None) continue;
                list.Add(row.label);
            }
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        /// <summary>Portrait keys the placeholder tool must provide: one per character with a real slot.</summary>
        public static List<string> PortraitKeysForPlaceholders(VNSpeakerMap map, string prefix)
        {
            var keys = new List<string>();
            if (map == null) return keys;
            var rows = map.Rows;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null || string.IsNullOrEmpty(row.characterId)) continue;
                if (row.defaultSlot == PortraitSlot.None) continue;
                keys.Add(VNImportNaming.PortraitKey(row.characterId, row.defaultEmotion, prefix));
            }
            keys.Sort((a, b) => string.CompareOrdinal(a, b));
            return keys;
        }
    }
}
