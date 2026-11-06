// REZONANCE VN ENGINE - settings persistence (UI/services module).
// JsonUtility DTO on Application.persistentDataPath. Loading is tolerant: a broken or unknown file
// yields defaults instead of an exception (ARCHITECTURE.md sections 6 and 10).
using System;
using System.IO;
using UnityEngine;
using Rezonance.VN.Core;

namespace Rezonance.VN.Services
{
    /// <summary>File backed <see cref="ISettingsStore"/>. Owns persistentDataPath/Rezonance/settings.json.</summary>
    public sealed class SettingsStore : ISettingsStore
    {
        private const string Area = "settings";

        /// <summary>Shape version of settings.json. Bump when fields are renamed or removed.</summary>
        public const int ConfigVersion = 1;

        /// <summary>JsonUtility wrapper so the format version travels with the payload.</summary>
        [Serializable]
        private sealed class SettingsEnvelope
        {
            public int formatVersion = ConfigVersion;
            public VNSettings settings;
        }

        private VNSettings current = CreateDefaults();
        private bool applyingSideEffects;

        /// <summary>True when the last Load() fell back to defaults because the file was unreadable.</summary>
        public bool LastLoadFailed { get; private set; }

        /// <summary>True when the last Save() could not write the file.</summary>
        public bool LastSaveFailed { get; private set; }

        public VNSettings Current => current;

        public event Action<VNSettings> Changed;

        /// <summary>Creates the store and reads the disk copy immediately.</summary>
        public SettingsStore() : this(true) { }

        /// <summary>Creates the store, optionally deferring the first read (bootstrap may load later).</summary>
        public SettingsStore(bool loadImmediately)
        {
            if (loadImmediately) Load();
        }

        /// <summary>Folder holding every Rezonance user file.</summary>
        public static string FolderPath => Path.Combine(Application.persistentDataPath, "Rezonance");

        /// <summary>Full path of settings.json.</summary>
        public static string FilePath => Path.Combine(FolderPath, "settings.json");

        /// <summary>Fresh default settings (a new instance every call).</summary>
        public static VNSettings CreateDefaults() => new VNSettings();

        // ------------------------------------------------------------------ contract

        /// <summary>Replaces the live settings, broadcasts Changed and applies engine side effects.</summary>
        public void Apply(VNSettings settings)
        {
            if (settings == null)
            {
                VNLog.Warn(Area, "Apply(null) ignored");
                return;
            }
            current = Sanitize(settings.Clone());
            Raise();
            ApplySideEffects(current);
        }

        /// <summary>Clone-mutate-apply-save helper for UI controls; never throws out of the callback.</summary>
        public void Update(Action<VNSettings> mutate)
        {
            if (mutate == null) return;
            var copy = current.Clone();
            try
            {
                mutate(copy);
            }
            catch (Exception e)
            {
                VNLog.Error(Area, "Settings mutation failed: " + e.Message);
                return;
            }
            Apply(copy);
            Save();
        }

        /// <summary>Writes the current settings; failures only log, the game keeps running.</summary>
        public void Save()
        {
            LastSaveFailed = false;
            try
            {
                Directory.CreateDirectory(FolderPath);
                var envelope = new SettingsEnvelope { formatVersion = ConfigVersion, settings = current };
                File.WriteAllText(FilePath, JsonUtility.ToJson(envelope, true));
                VNLog.Info(Area, "Settings saved to " + FilePath);
            }
            catch (Exception e)
            {
                LastSaveFailed = true;
                VNLog.Error(Area, "Settings save failed: " + e.Message);
            }
        }

        /// <summary>Reads settings.json; unknown fields are ignored by JsonUtility, garbage falls back to defaults.</summary>
        public void Load()
        {
            LastLoadFailed = false;
            try
            {
                if (!File.Exists(FilePath))
                {
                    VNLog.Info(Area, "No settings file yet, using defaults");
                    current = CreateDefaults();
                    Raise();
                    return;
                }

                var json = File.ReadAllText(FilePath);
                var envelope = JsonUtility.FromJson<SettingsEnvelope>(json);
                if (envelope == null || envelope.settings == null)
                {
                    // Older layouts wrote the bare settings object - try that before giving up.
                    var bare = JsonUtility.FromJson<VNSettings>(json);
                    if (bare == null) throw new InvalidDataException("settings.json has no recognizable payload");
                    envelope = new SettingsEnvelope { formatVersion = 0, settings = bare };
                }

                if (envelope.formatVersion > ConfigVersion)
                    VNLog.Warn(Area, "settings.json comes from a newer build (v" + envelope.formatVersion + ")");

                current = Sanitize(envelope.settings);
                VNLog.Info(Area, "Settings loaded (v" + envelope.formatVersion + ")");
                Raise();
                ApplySideEffects(current);
            }
            catch (Exception e)
            {
                LastLoadFailed = true;
                current = CreateDefaults();
                VNLog.Error(Area, "Settings load failed, defaults restored: " + e.Message);
                Raise();
            }
        }

        /// <summary>Restores factory values and persists them.</summary>
        public void ResetToDefaults()
        {
            Apply(CreateDefaults());
            Save();
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Clamps every field into its documented range so a hand-edited file cannot break the game.</summary>
        public static VNSettings Sanitize(VNSettings s)
        {
            if (s == null) return CreateDefaults();
            s.masterVolume = Clamp01(s.masterVolume);
            s.musicVolume = Clamp01(s.musicVolume);
            s.ambienceVolume = Clamp01(s.ambienceVolume);
            s.sfxVolume = Clamp01(s.sfxVolume);
            s.voiceVolume = Clamp01(s.voiceVolume);
            s.textSpeedCps = Clamp(s.textSpeedCps, 0f, 400f);
            s.autoNodeDelaySeconds = Clamp(s.autoNodeDelaySeconds, 0.1f, 20f);
            s.autoReadDelaySeconds = Clamp(s.autoReadDelaySeconds, 0f, 20f);
            s.backlogMaxEntries = (int)Clamp(s.backlogMaxEntries, 20f, 5000f);
            s.portraitDimming = Clamp01(s.portraitDimming);
            return s;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        private void Raise()
        {
            try { Changed?.Invoke(current); }
            catch (Exception e) { VNLog.Error(Area, "Changed handler failed: " + e.Message); }
        }

        /// <summary>Pushes the values the store is allowed to touch directly (log flag, fullscreen).</summary>
        private void ApplySideEffects(VNSettings s)
        {
            if (applyingSideEffects) return;
            applyingSideEffects = true;
            try
            {
                VNLog.Dev = s.devVerboseLog || Debug.isDebugBuild;
            }
            catch (Exception e) { VNLog.Warn(Area, "Log flag update failed: " + e.Message); }

            try
            {
                if (s.fullscreen && Screen.fullScreen != true) Screen.fullScreen = true;
                else if (!s.fullscreen && Screen.fullScreen) Screen.fullScreen = false;
            }
            catch (Exception e) { VNLog.Warn(Area, "Fullscreen update failed: " + e.Message); }

            applyingSideEffects = false;
        }
    }
}
