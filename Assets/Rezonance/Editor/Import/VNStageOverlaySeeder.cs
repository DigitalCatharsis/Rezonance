// REZONANCE VN EDITOR - staging overlay seeder.
// Seeds VNStageOverlay.asset with one Scene row per scene (backdrop key from the scene heading) and one
// Node row per mini game marker. Existing rows are touched only where a field is still empty: hand
// authored fades, portraits and audio cues are never overwritten.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Rezonance.VN.Core;

namespace Rezonance.VN.Import
{
    /// <summary>Result of one overlay seeding pass.</summary>
    public sealed class VNOverlaySeedResult
    {
        public int NewSceneRows;
        public int NewMiniGameRows;
        public int TouchedSceneRows;
        public readonly List<string> MiniGameIds = new List<string>();
    }

    /// <summary>Creates and refreshes the overlay asset next to the generated script data.</summary>
    public static class VNStageOverlaySeeder
    {
        /// <summary>Loads the overlay asset or creates a fresh one at the given project path.</summary>
        public static VNStageOverlay LoadOrCreate(string assetPath)
        {
            var overlay = AssetDatabase.LoadAssetAtPath<VNStageOverlay>(assetPath);
            if (overlay != null) return overlay;
            overlay = ScriptableObject.CreateInstance<VNStageOverlay>();
            overlay.name = "VNStageOverlay";
            AssetDatabase.CreateAsset(overlay, assetPath);
            return overlay;
        }

        /// <summary>Seeds missing rows, fills only the empty backdrop keys of existing ones.</summary>
        public static VNOverlaySeedResult Seed(VNStageOverlay overlay, VNScriptData data, VNImportSettings settings)
        {
            var result = new VNOverlaySeedResult();
            if (overlay == null || data == null || data.scenes == null) return result;
            var prefix = settings != null ? settings.backdropPrefix : "bg_";

            for (var s = 0; s < data.scenes.Count; s++)
            {
                var scene = data.scenes[s];
                if (scene == null || string.IsNullOrEmpty(scene.id)) continue;

                var key = VNImportNaming.BackdropKey(scene.title, prefix);
                var existing = FindRow(overlay, VNMatchKind.Scene, scene.id);
                if (existing == null)
                {
                    overlay.UpsertRow(new VNStageCueRow
                    {
                        match = VNMatchKind.Scene,
                        matchId = scene.id,
                        backdropKey = key,
                        // Left false on purpose: the importer must not decide the transition of a scene.
                        fadeSpecified = false,
                        note = "seeded: " + scene.title
                    });
                    result.NewSceneRows++;
                }
                else if (string.IsNullOrEmpty(existing.backdropKey))
                {
                    existing.backdropKey = key;
                    result.TouchedSceneRows++;
                }
            }

            for (var i = 0; i < data.scenes.Count; i++)
            {
                var scene = data.scenes[i];
                if (scene == null || scene.branches == null) continue;
                for (var b = 0; b < scene.branches.Count; b++)
                {
                    var branch = scene.branches[b];
                    if (branch == null || branch.nodes == null) continue;
                    for (var n = 0; n < branch.nodes.Count; n++)
                    {
                        var node = branch.nodes[n];
                        if (node == null || node.kind != VNNodeKind.MiniGame) continue;
                        var miniGameId = VNImportNaming.MiniGameId(node.id);
                        if (FindRow(overlay, VNMatchKind.Node, node.id) != null) continue;
                        overlay.UpsertRow(new VNStageCueRow
                        {
                            match = VNMatchKind.Node,
                            matchId = node.id,
                            miniGameId = miniGameId,
                            fadeSpecified = false,
                            note = "mini game marker, branch: " + branch.title
                        });
                        result.NewMiniGameRows++;
                        result.MiniGameIds.Add(miniGameId + "  <-  " + node.id);
                    }
                }
            }

            overlay.Reindex();
            EditorUtility.SetDirty(overlay);
            return result;
        }

        static VNStageCueRow FindRow(VNStageOverlay overlay, VNMatchKind kind, string matchId)
        {
            var rows = overlay.Rows;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row != null && row.match == kind && row.matchId == matchId) return row;
            }
            return null;
        }

        /// <summary>Backdrop keys the placeholder tool must cover (one per scene row with a key).</summary>
        public static List<string> BackdropKeys(VNStageOverlay overlay)
        {
            var keys = new List<string>();
            if (overlay == null) return keys;
            var rows = overlay.Rows;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null || row.match != VNMatchKind.Scene || string.IsNullOrEmpty(row.backdropKey)) continue;
                if (!keys.Contains(row.backdropKey)) keys.Add(row.backdropKey);
            }
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }
    }
}
