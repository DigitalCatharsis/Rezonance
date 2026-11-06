// REZONANCE VN ENGINE - save game persistence (UI/services module).
// Layout (ARCHITECTURE.md section 6): persistentDataPath/Rezonance/saves/slot_0..slot_11.json,
// autosave.json, quicksave.json plus autosave_1..3 rotation generations.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Rezonance.VN.Core;

namespace Rezonance.VN.Services
{
    /// <summary>Health of a slot file, surfaced to the save/load screen.</summary>
    public enum VNSlotStatus
    {
        /// <summary>No file on disk.</summary>
        Missing = 0,
        /// <summary>Readable envelope with a known format version.</summary>
        Ok = 1,
        /// <summary>File exists but cannot be parsed - shown as corrupted.</summary>
        Corrupted = 2,
        /// <summary>File was written by a newer build; the engine refuses to open it.</summary>
        NewerVersion = 3
    }

    /// <summary>JsonUtility envelope around a game state. Public so tooling can inspect saves.</summary>
    [Serializable]
    public sealed class SaveEnvelope
    {
        public int formatVersion = SaveService.SaveFormatVersion;
        public string slotId;
        public long savedAtUtcTicks;
        public float playtimeSeconds;
        public string actId;
        public string sceneId;
        public string branchId;
        public string nodeId;
        /// <summary>Localization key of the saved node, kept for previews when the state is unreadable.</summary>
        public string previewKey;
        /// <summary>Source (RU) text of the saved node, last-resort preview.</summary>
        public string previewSource;
        public string languageTag;
        public VNGameState state;
    }

    /// <summary>File backed <see cref="ISaveService"/>: 12 manual slots plus autosave (3 generations) and quicksave.</summary>
    public sealed class SaveService : ISaveService
    {
        private const string Area = "save";

        /// <summary>Format version written into every envelope.</summary>
        public const int SaveFormatVersion = 1;

        /// <summary>Number of player visible manual slots: slot_0 .. slot_11.</summary>
        public const int ManualSlotCount = 12;

        /// <summary>How many rotated autosave copies are kept next to autosave.json.</summary>
        public const int AutosaveGenerations = 3;

        /// <summary>Slot id of the rolling autosave.</summary>
        public const string AutosaveSlot = "autosave";

        /// <summary>Slot id of the F5 quicksave.</summary>
        public const string QuicksaveSlot = "quicksave";

        private VNDataRegistry registry;
        private ILocalizer localizer;
        private bool foldersReady;

        /// <summary>Last IO or parse error message, for the debug overlay.</summary>
        public string LastError { get; private set; }

        /// <summary>Raised after any successful write or delete so open screens can refresh.</summary>
        public event Action<string> SlotChanged;

        public string AutosaveSlotId => AutosaveSlot;

        public string QuicksaveSlotId => QuicksaveSlot;

        public SaveService() { }

        /// <summary>Creates the service with its read-only dependencies already bound.</summary>
        public SaveService(VNDataRegistry registry, ILocalizer localizer)
        {
            Initialize(registry, localizer);
        }

        // ------------------------------------------------------------------ setup

        /// <summary>Contract initialization: no data sources, previews degrade to raw ids.</summary>
        public void Initialize()
        {
            Initialize(null, null);
        }

        /// <summary>Binds the script registry and localizer used to describe slots; call once at boot.</summary>
        public void Initialize(VNDataRegistry registry, ILocalizer localizer)
        {
            this.registry = registry;
            this.localizer = localizer;
            if (registry == null) VNLog.Warn(Area, "No VNDataRegistry bound - slot scene titles will fall back to ids");
            if (localizer == null) VNLog.Warn(Area, "No ILocalizer bound - slot previews will show source text");
            EnsureFolder();
        }

        /// <summary>Replaces the data sources after a reimport or a language table reload.</summary>
        public void SetDataSources(VNDataRegistry registry, ILocalizer localizer)
        {
            if (registry != null) this.registry = registry;
            if (localizer != null) this.localizer = localizer;
        }

        /// <summary>persistentDataPath/Rezonance/saves - never an absolute platform specific path.</summary>
        public static string FolderPath => Path.Combine(Application.persistentDataPath, "Rezonance", "saves");

        private void EnsureFolder()
        {
            if (foldersReady) return;
            try
            {
                Directory.CreateDirectory(FolderPath);
                foldersReady = true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                VNLog.Error(Area, "Cannot create save folder: " + e.Message);
            }
        }

        /// <summary>Disk file behind a slot id; ids are sanitized so nothing escapes the folder.</summary>
        public string SlotPath(string slotId)
        {
            return Path.Combine(FolderPath, SafeSlotId(slotId) + ".json");
        }

        /// <summary>Autosave rotation copies: autosave_1 is the freshest history entry.</summary>
        public string AutosaveHistorySlotId(int generation)
        {
            var g = Mathf.Clamp(generation, 1, AutosaveGenerations);
            return AutosaveSlot + "_" + g.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>True for slot_0 .. slot_11.</summary>
        public static bool IsManualSlot(string slotId)
        {
            if (string.IsNullOrEmpty(slotId) || !slotId.StartsWith("slot_", StringComparison.Ordinal)) return false;
            return int.TryParse(slotId.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                   && n >= 0 && n < ManualSlotCount;
        }

        /// <summary>True for autosave, quicksave and the autosave generations.</summary>
        public static bool IsSystemSlot(string slotId)
        {
            if (string.IsNullOrEmpty(slotId)) return false;
            return slotId == QuicksaveSlot
                   || slotId == AutosaveSlot
                   || slotId.StartsWith(AutosaveSlot + "_", StringComparison.Ordinal);
        }

        private static string SafeSlotId(string slotId)
        {
            if (string.IsNullOrEmpty(slotId)) return "slot_unknown";
            var sb = new StringBuilder(slotId.Length);
            foreach (var c in slotId)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
            }
            var clean = sb.ToString();
            return clean.Length == 0 ? "slot_unknown" : clean;
        }

        // ------------------------------------------------------------------ write

        /// <summary>Serializes the state into the slot. Writing to autosave first rotates the history.</summary>
        public bool Save(string slotId, VNGameState state)
        {
            if (state == null)
            {
                VNLog.Warn(Area, "Save(" + slotId + ") ignored: null state");
                return false;
            }
            EnsureFolder();
            var id = SafeSlotId(slotId);
            var path = SlotPath(id);
            try
            {
                if (id == AutosaveSlot) RotateAutosaveHistory();

                state.formatVersion = SaveFormatVersion;
                if (state.savedAtUtcTicks <= 0) state.savedAtUtcTicks = DateTime.UtcNow.Ticks;

                var envelope = BuildEnvelope(id, state);
                File.WriteAllText(path, JsonUtility.ToJson(envelope));
                LastError = null;
                VNLog.Info(Area, "Saved slot " + id + " (" + path + ")");
                Notify(id);
                return true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                VNLog.Error(Area, "Save(" + id + ") failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Quicksave convenience (input binding F5).</summary>
        public bool QuickSave(VNGameState state) => Save(QuicksaveSlot, state);

        /// <summary>Autosave convenience (engine calls it at branch/menu/scene boundaries).</summary>
        public bool Autosave(VNGameState state) => Save(AutosaveSlot, state);

        private SaveEnvelope BuildEnvelope(string id, VNGameState state)
        {
            var envelope = new SaveEnvelope
            {
                formatVersion = SaveFormatVersion,
                slotId = id,
                savedAtUtcTicks = state.savedAtUtcTicks,
                playtimeSeconds = state.playtimeSeconds,
                actId = state.actId,
                sceneId = state.sceneId,
                branchId = state.branchId,
                nodeId = state.nodeId,
                languageTag = state.language.ToString(),
                state = state
            };

            VNNode node = null;
            if (registry != null) registry.TryGetNode(state.nodeId, out node);
            if (node != null)
            {
                envelope.previewKey = node.id;
                envelope.previewSource = node.text;
            }
            return envelope;
        }

        /// <summary>Shifts autosave.json through autosave_1..3 so the last three autosaves survive.</summary>
        public void RotateAutosaveHistory()
        {
            try
            {
                var currentPath = SlotPath(AutosaveSlot);
                if (!File.Exists(currentPath)) return;

                for (var g = AutosaveGenerations - 1; g >= 1; g--)
                {
                    var from = SlotPath(AutosaveHistorySlotId(g));
                    var to = SlotPath(AutosaveHistorySlotId(g + 1));
                    if (File.Exists(from)) File.Copy(from, to, true);
                }
                File.Copy(currentPath, SlotPath(AutosaveHistorySlotId(1)), true);
            }
            catch (Exception e)
            {
                // Rotation is best effort: a failed copy must never lose the write the player asked for.
                VNLog.Warn(Area, "Autosave rotation skipped: " + e.Message);
            }
        }

        // ------------------------------------------------------------------ read

        /// <summary>Reads a slot. False when missing, unreadable or written by a newer build.</summary>
        public bool TryLoad(string slotId, out VNGameState state)
        {
            state = null;
            var envelope = ReadEnvelope(SafeSlotId(slotId));
            if (envelope == null || envelope.state == null) return false;
            if (envelope.formatVersion > SaveFormatVersion)
            {
                VNLog.Warn(Area, "Slot " + slotId + " comes from a newer version and was not loaded");
                return false;
            }
            state = envelope.state;
            return true;
        }

        /// <summary>F5 equivalent of TryLoad for the quicksave slot.</summary>
        public bool QuickLoad(out VNGameState state) => TryLoad(QuicksaveSlot, out state);

        /// <summary>Reads one autosave generation (1 = freshest) written by the rotation.</summary>
        public bool TryLoadAutosaveHistory(int generation, out VNGameState state)
            => TryLoad(AutosaveHistorySlotId(generation), out state);

        private SaveEnvelope ReadEnvelope(string slotId)
        {
            var path = SlotPath(slotId);
            try
            {
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                var envelope = JsonUtility.FromJson<SaveEnvelope>(json);
                if (envelope == null) return null;
                if (string.IsNullOrEmpty(envelope.slotId)) envelope.slotId = slotId;
                return envelope;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                VNLog.Warn(Area, "Slot " + slotId + " is unreadable: " + e.Message);
                return CorruptedMarker(slotId);
            }
        }

        // A parse failure still has to produce a row so the player can delete the file.
        private static SaveEnvelope CorruptedMarker(string slotId)
        {
            return new SaveEnvelope { formatVersion = int.MaxValue, slotId = slotId };
        }

        /// <summary>Health of a slot file without throwing.</summary>
        public VNSlotStatus GetStatus(string slotId)
        {
            var id = SafeSlotId(slotId);
            var path = SlotPath(id);
            try
            {
                if (!File.Exists(path)) return VNSlotStatus.Missing;
                var envelope = ReadEnvelope(id);
                if (envelope == null) return VNSlotStatus.Corrupted;
                if (envelope.formatVersion == int.MaxValue) return VNSlotStatus.Corrupted;
                if (envelope.formatVersion > SaveFormatVersion) return VNSlotStatus.NewerVersion;
                return VNSlotStatus.Ok;
            }
            catch (Exception e)
            {
                VNLog.Warn(Area, "Status of " + id + " failed: " + e.Message);
                return VNSlotStatus.Corrupted;
            }
        }

        public bool Exists(string slotId)
        {
            try
            {
                return File.Exists(SlotPath(SafeSlotId(slotId)));
            }
            catch (Exception e)
            {
                VNLog.Warn(Area, "Exists(" + slotId + ") failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Deletes a slot file; the autosave generations go with the autosave slot.</summary>
        public bool Delete(string slotId)
        {
            var id = SafeSlotId(slotId);
            var ok = false;
            try
            {
                var path = SlotPath(id);
                if (File.Exists(path)) File.Delete(path);
                ok = !File.Exists(path);

                if (id == AutosaveSlot)
                {
                    for (var g = 1; g <= AutosaveGenerations; g++)
                    {
                        var gen = SlotPath(AutosaveHistorySlotId(g));
                        if (File.Exists(gen)) File.Delete(gen);
                    }
                }
                LastError = null;
                VNLog.Info(Area, "Deleted slot " + id);
                Notify(id);
                return ok;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                VNLog.Error(Area, "Delete(" + id + ") failed: " + e.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------ listing

        /// <summary>Rows for slot_0..slot_11, autosave and quicksave, in that display order.</summary>
        public IReadOnlyList<SaveSlotInfo> ListSlots() => ListSlots(false);

        /// <summary>Rows including the autosave rotation copies when the load screen shows history.</summary>
        public IReadOnlyList<SaveSlotInfo> ListSlots(bool includeAutosaveHistory)
        {
            EnsureFolder();
            var list = new List<SaveSlotInfo>(ManualSlotCount + AutosaveGenerations + 2);
            for (var i = 0; i < ManualSlotCount; i++)
                list.Add(Describe("slot_" + i.ToString(CultureInfo.InvariantCulture)));

            list.Add(Describe(AutosaveSlot));
            if (includeAutosaveHistory)
            {
                for (var g = 1; g <= AutosaveGenerations; g++)
                    list.Add(Describe(AutosaveHistorySlotId(g)));
            }
            list.Add(Describe(QuicksaveSlot));
            return list;
        }

        /// <summary>Builds one row: timestamp, playtime, scene title and preview resolved through registry + localizer.</summary>
        public SaveSlotInfo Describe(string slotId)
        {
            var id = SafeSlotId(slotId);
            var info = new SaveSlotInfo { slotId = id, displayName = DisplayName(id), exists = false };

            var status = GetStatus(id);
            if (status == VNSlotStatus.Missing) return info;
            info.exists = status != VNSlotStatus.Missing;

            if (status == VNSlotStatus.Corrupted)
            {
                info.corrupted = true;
                info.previewText = T("ui.save.corrupted");
                return info;
            }

            var envelope = ReadEnvelope(id);
            if (envelope == null || status == VNSlotStatus.Corrupted)
            {
                info.corrupted = true;
                info.previewText = T("ui.save.corrupted");
                return info;
            }

            info.savedAtUtcTicks = envelope.savedAtUtcTicks != 0 ? envelope.savedAtUtcTicks : TicksOf(envelope.slotId);
            info.playtimeSeconds = envelope.playtimeSeconds;
            info.sceneTitle = SceneTitle(envelope);
            info.previewText = status == VNSlotStatus.NewerVersion
                ? T("ui.save.newer_version")
                : Preview(envelope);
            return info;
        }

        private static long TicksOf(string slotId)
        {
            // A file without a timestamp still must not show DateTime(0).
            try
            {
                return new FileInfo(SlotPathStatic(slotId)).LastWriteTimeUtc.Ticks;
            }
            catch (Exception e)
            {
                VNLog.Warn(Area, "Timestamp of slot " + slotId + " unavailable: " + e.Message);
                return DateTime.UtcNow.Ticks;
            }
        }

        private static string SlotPathStatic(string slotId)
        {
            return Path.Combine(FolderPath, SafeSlotId(slotId) + ".json");
        }

        private string DisplayName(string slotId)
        {
            if (IsManualSlot(slotId))
                return T("ui.save.slot") + " " + slotId.Substring(5);
            if (slotId == AutosaveSlot) return T("ui.save.autosave");
            if (slotId == QuicksaveSlot) return T("ui.save.quicksave");
            if (slotId.StartsWith(AutosaveSlot + "_", StringComparison.Ordinal))
                return T("ui.save.autosave") + " " + slotId.Substring(AutosaveSlot.Length + 1);
            return slotId;
        }

        private string SceneTitle(SaveEnvelope envelope)
        {
            var sceneId = envelope.state != null && !string.IsNullOrEmpty(envelope.state.sceneId)
                ? envelope.state.sceneId
                : envelope.sceneId;
            if (string.IsNullOrEmpty(sceneId)) return T("ui.save.unknown_scene");

            if (registry != null && registry.TryGetScene(sceneId, out var scene) && scene != null
                && !string.IsNullOrEmpty(scene.title))
                return scene.title;

            // Scene titles are source text; a localized row may exist as "<sceneId>.title".
            if (localizer != null && localizer.TryGet(sceneId + ".title", out var localized) && !string.IsNullOrEmpty(localized))
                return localized;

            return sceneId;
        }

        private string Preview(SaveEnvelope envelope)
        {
            var nodeId = envelope.state != null && !string.IsNullOrEmpty(envelope.state.nodeId)
                ? envelope.state.nodeId
                : envelope.nodeId;

            if (localizer != null && !string.IsNullOrEmpty(nodeId) && localizer.TryGet(nodeId, out var localized)
                && !string.IsNullOrEmpty(localized))
                return Truncate(localized);

            VNNode node = null;
            if (registry != null && !string.IsNullOrEmpty(nodeId)) registry.TryGetNode(nodeId, out node);
            var source = node != null && !string.IsNullOrEmpty(node.text) ? node.text : envelope.previewSource;
            if (string.IsNullOrEmpty(source)) return nodeId ?? string.Empty;
            return Truncate(source);
        }

        private static string Truncate(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var flat = raw.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length <= 160 ? flat : flat.Substring(0, 160) + "...";
        }

        /// <summary>True when at least one readable or corrupted save file exists - drives Continue.</summary>
        public bool HasAnySave()
        {
            EnsureFolder();
            try
            {
                if (!Directory.Exists(FolderPath)) return false;
                return Directory.GetFiles(FolderPath, "*.json").Length > 0;
            }
            catch (Exception e)
            {
                VNLog.Warn(Area, "HasAnySave failed: " + e.Message);
                return false;
            }
        }

        private string T(string key)
        {
            if (localizer == null) return key;
            try { return localizer.Get(key); }
            catch (Exception e)
            {
                VNLog.Warn(Area, "Localizer failed for " + key + ": " + e.Message);
                return key;
            }
        }

        private void Notify(string slotId)
        {
            try { SlotChanged?.Invoke(slotId); }
            catch (Exception e) { VNLog.Error(Area, "SlotChanged handler failed: " + e.Message); }
        }
    }
}
