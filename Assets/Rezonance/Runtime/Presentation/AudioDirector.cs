// REZONANCE VN ENGINE - four bus audio with unscaled-time fades.
// Music and ambience crossfade between two sources, sfx uses a small pool, voice is single shot.
using System;
using System.Collections.Generic;
using Rezonance.VN.Core;
using UnityEngine;

namespace Rezonance.VN.Presentation
{
    /// <summary>Audio director implementing IAudioDirector over plain AudioSources.</summary>
    public sealed class AudioDirector : IAudioDirector, IDisposable
    {
        /// <summary>Name prefix of the generated channel objects.</summary>
        public const string ElementName = "VN_Audio";

        /// <summary>Fade length used when the caller passes a non positive value.</summary>
        public const float DefaultFadeSeconds = 1f;

        private const string LogArea = "audio";
        private const int SfxPoolSize = 4;

        private static readonly VNAudioBus[] BusOrder =
        {
            VNAudioBus.Music, VNAudioBus.Ambience, VNAudioBus.Sfx, VNAudioBus.Voice
        };

        private readonly AssetLibrary assets;
        private readonly Dictionary<VNAudioBus, Channel> channels = new Dictionary<VNAudioBus, Channel>();
        private readonly float[] busVolume = new float[5];
        private readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);

        private PresentationHost host;
        private Transform parent;
        private ISettingsStore store;
        private bool subscribed;
        private float master = 1f;
        private bool suspended;

        /// <summary>Creates the director; null arguments fall back to the shared library and host.</summary>
        public AudioDirector(AssetLibrary assetLibrary = null, PresentationHost animationHost = null)
        {
            assets = assetLibrary ?? AssetLibrary.Shared;
            host = animationHost;
            for (var i = 0; i < busVolume.Length; i++) busVolume[i] = 1f;
        }

        /// <summary>Bus level of the master channel, mirrored from the settings store.</summary>
        public float MasterVolume => master;

        /// <summary>True while playback is muted without losing the sample positions.</summary>
        public bool IsSuspended => suspended;

        /// <summary>Creates the channel objects. Audio needs no canvas, the parent is only for the hierarchy.</summary>
        public void Init(Transform optionalParent = null)
        {
            parent = optionalParent;
            foreach (var bus in BusOrder) EnsureChannel(bus);
            VNLog.Info(LogArea, "audio buses ready");
        }

        /// <summary>Reads the levels from a settings store and follows later changes. Calling it twice is safe.</summary>
        public void BindSettings(ISettingsStore settingsStore)
        {
            if (ReferenceEquals(store, settingsStore) && subscribed) return;
            UnbindSettings();
            store = settingsStore;
            if (store == null) return;
            ApplyStore(store.Current);
            store.Changed += OnSettingsChanged;
            subscribed = true;
        }

        /// <summary>Removes the settings subscription; call from OnDestroy of the owner.</summary>
        public void UnbindSettings()
        {
            if (!subscribed || store == null) return;
            store.Changed -= OnSettingsChanged;
            subscribed = false;
        }

        /// <summary>Starts a cue. Playing the same cue again on music or ambience does not restart the fade.</summary>
        public void Play(VNAudioBus bus, string cueId, float fadeSeconds, bool loop)
        {
            if (bus == VNAudioBus.Master || string.IsNullOrEmpty(cueId))
            {
                WarnOnce("play-" + bus + "-" + cueId, "ignored Play on bus " + bus + " with cue '" + cueId + "'");
                return;
            }

            var channel = EnsureChannel(bus);
            if (channel == null) return;

            var seconds = fadeSeconds > 0f ? fadeSeconds : (channel.Crossfade ? DefaultFadeSeconds : 0f);
            // TODO(contract): AudioCue carries no loop flag (VNAudioRow.loop is dropped by the overlay merge),
            // so music and ambience loop unless the caller explicitly passes loop = false with a stop cue.
            var shouldLoop = loop || channel.Crossfade;

            if (channel.Crossfade)
            {
                var live = channel.FindPlaying(cueId);
                if (live >= 0 && channel.Slots[live].target >= 0.999f) return;

                var target = channel.PickQuietSlot();
                Start(channel, target, cueId, shouldLoop, seconds);
                for (var i = 0; i < channel.Slots.Length; i++)
                {
                    if (i == target) continue;
                    Fade(channel, i, 0f, seconds, true);
                }
                return;
            }

            // sfx and voice: single shot, no looping unless the caller insists.
            var free = channel.PickFreeSlot();
            Start(channel, free, cueId, shouldLoop, seconds);
            if (bus == VNAudioBus.Voice)
            {
                for (var i = 0; i < channel.Slots.Length; i++)
                    if (i != free) Fade(channel, i, 0f, 0f, true);
            }
        }

        /// <summary>Stops every cue on a bus with a fade.</summary>
        public void Stop(VNAudioBus bus, float fadeSeconds)
        {
            if (!channels.TryGetValue(bus, out var channel)) return;
            var seconds = fadeSeconds > 0f ? fadeSeconds : 0f;
            for (var i = 0; i < channel.Slots.Length; i++) Fade(channel, i, 0f, seconds, true);
            channel.RequestedCue = null;
        }

        /// <summary>Stops every bus.</summary>
        public void StopAll(float fadeSeconds)
        {
            foreach (var pair in channels)
                for (var i = 0; i < pair.Value.Slots.Length; i++)
                    Fade(pair.Value, i, 0f, fadeSeconds > 0f ? fadeSeconds : 0f, true);
        }

        /// <summary>Sets a bus level. Master scales everything.</summary>
        public void SetBusVolume(VNAudioBus bus, float volume01)
        {
            var value = Mathf.Clamp01(volume01);
            if (bus == VNAudioBus.Master) master = value;
            else busVolume[(int)bus] = value;
            foreach (var pair in channels)
                for (var i = 0; i < pair.Value.Slots.Length; i++) ApplyVolume(pair.Value, i);
        }

        /// <summary>Reads a bus level.</summary>
        public float GetBusVolume(VNAudioBus bus)
        {
            return bus == VNAudioBus.Master ? master : busVolume[(int)bus];
        }

        /// <summary>Cue currently requested on a bus, null when the bus is silent.</summary>
        public string CurrentCue(VNAudioBus bus)
        {
            return channels.TryGetValue(bus, out var channel) ? channel.RequestedCue : null;
        }

        /// <summary>Mutes everything while keeping positions, for the pause menu.</summary>
        public void SetSuspended(bool value)
        {
            if (suspended == value) return;
            suspended = value;
            foreach (var pair in channels)
            {
                foreach (var slot in pair.Value.Slots)
                {
                    if (slot.source == null) continue;
                    if (suspended) slot.source.Pause();
                    else slot.source.UnPause();
                }
            }
        }

        /// <summary>Stops every cue instantly and re-reads the settings; used when a save is restored.</summary>
        public void Reset()
        {
            foreach (var pair in channels)
            {
                for (var i = 0; i < pair.Value.Slots.Length; i++)
                {
                    Cancel(pair.Value, i);
                    if (pair.Value.Slots[i].source != null)
                    {
                        pair.Value.Slots[i].source.Stop();
                        pair.Value.Slots[i].source.volume = 0f;
                    }
                    pair.Value.Slots[i].gain = 0f;
                    pair.Value.Slots[i].target = 0f;
                    pair.Value.Slots[i].cue = null;
                }
                pair.Value.RequestedCue = null;
            }
            if (store != null) ApplyStore(store.Current);
        }

        /// <summary>Unsubscribes and destroys the channel objects.</summary>
        public void Dispose()
        {
            UnbindSettings();
            foreach (var pair in channels)
            {
                for (var i = 0; i < pair.Value.Slots.Length; i++) Cancel(pair.Value, i);
                if (pair.Value.Root != null) UnityEngine.Object.Destroy(pair.Value.Root);
            }
            channels.Clear();
            parent = null;
        }

        private void OnSettingsChanged(VNSettings settings) => ApplyStore(settings);

        private void ApplyStore(VNSettings settings)
        {
            if (settings == null) return;
            master = Mathf.Clamp01(settings.masterVolume);
            busVolume[(int)VNAudioBus.Music] = Mathf.Clamp01(settings.musicVolume);
            busVolume[(int)VNAudioBus.Ambience] = Mathf.Clamp01(settings.ambienceVolume);
            busVolume[(int)VNAudioBus.Sfx] = Mathf.Clamp01(settings.sfxVolume);
            busVolume[(int)VNAudioBus.Voice] = Mathf.Clamp01(settings.voiceVolume);
            foreach (var pair in channels)
                for (var i = 0; i < pair.Value.Slots.Length; i++) ApplyVolume(pair.Value, i);
        }

        private void Start(Channel channel, int index, string cueId, bool loop, float fadeSeconds)
        {
            var slot = channel.Slots[index];
            var clip = assets.GetAudioClip(channel.Bus, cueId);
            if (clip == null)
            {
                WarnOnce("clip-" + cueId, "audio library returned no clip for " + cueId);
                return;
            }

            if (slot.source == null) return;
            slot.cue = cueId;
            slot.source.clip = clip;
            slot.source.loop = loop;
            slot.source.volume = 0f;
            if (!slot.source.isPlaying) slot.source.Play();
            channel.RequestedCue = cueId;
            Fade(channel, index, 1f, fadeSeconds, false);
        }

        private void Fade(Channel channel, int index, float target, float seconds, bool stopWhenDone)
        {
            var slot = channel.Slots[index];
            Cancel(channel, index);
            slot.target = Mathf.Clamp01(target);
            slot.stopWhenDone = stopWhenDone;

            var h = ResolveHost();
            if (h == null || seconds <= 0f || Mathf.Abs(slot.gain - slot.target) < 0.0005f)
            {
                slot.gain = slot.target;
                ApplyVolume(channel, index);
                if (slot.stopWhenDone && slot.gain <= 0.0005f) Halt(channel, index);
                return;
            }

            slot.from = slot.gain;
            slot.duration = Mathf.Max(0.01f, seconds);
            slot.elapsed = 0f;
            var capturedIndex = index;
            var capturedChannel = channel;
            slot.tick = h.Schedule(dt => Tick(capturedChannel, capturedIndex, dt));
        }

        private void Tick(Channel channel, int index, float deltaTime)
        {
            var slot = channel.Slots[index];
            slot.elapsed += deltaTime;
            var t = Mathf.Clamp01(slot.elapsed / slot.duration);
            slot.gain = Mathf.Lerp(slot.from, slot.target, t);
            ApplyVolume(channel, index);
            if (t < 1f) return;

            slot.gain = slot.target;
            ApplyVolume(channel, index);
            Cancel(channel, index);
            if (slot.stopWhenDone && slot.gain <= 0.0005f) Halt(channel, index);
        }

        private void Halt(Channel channel, int index)
        {
            var slot = channel.Slots[index];
            if (slot.source != null && slot.source.isPlaying) slot.source.Stop();
            slot.cue = null;
            slot.target = 0f;
        }

        private void ApplyVolume(Channel channel, int index)
        {
            var slot = channel.Slots[index];
            if (slot.source == null) return;
            var level = slot.gain * busVolume[(int)channel.Bus] * master;
            slot.source.volume = Mathf.Clamp01(level);
        }

        private void Cancel(Channel channel, int index)
        {
            var slot = channel.Slots[index];
            if (slot.tick == 0) return;
            var h = host != null && host.gameObject != null ? host : PresentationHost.Instance;
            if (h != null) h.Cancel(slot.tick);
            slot.tick = 0;
        }

        private Channel EnsureChannel(VNAudioBus bus)
        {
            if (channels.TryGetValue(bus, out var existing)) return existing;

            var h = ResolveHost();
            if (h == null)
            {
                WarnOnce("nohost", "no PresentationHost available, audio fades apply instantly");
                return null;
            }
            if (host == null) host = h;

            var go = new GameObject(ElementName + "_" + bus.ToString().ToLowerInvariant());
            if (parent != null) go.transform.SetParent(parent, false);

            var slotCount = bus == VNAudioBus.Sfx ? SfxPoolSize : bus == VNAudioBus.Voice ? 1 : 2;
            var slots = new Slot[slotCount];
            for (var i = 0; i < slotCount; i++)
            {
                var source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 0f;
                source.volume = 0f;
                slots[i] = new Slot { source = source };
            }

            var channel = new Channel
            {
                bus = bus,
                root = go,
                slots = slots,
                crossfade = bus == VNAudioBus.Music || bus == VNAudioBus.Ambience
            };
            channels[bus] = channel;
            return channel;
        }

        private PresentationHost ResolveHost()
        {
            if (host != null && host.gameObject != null) return host;
            host = PresentationHost.EnsureExists();
            return host;
        }

        private void WarnOnce(string identity, string message)
        {
            if (!warned.Add(identity)) return;
            VNLog.Warn(LogArea, message);
        }

        private sealed class Slot
        {
            public AudioSource source;
            public string cue;
            public float gain;
            public float target;
            public float from;
            public float duration = 1f;
            public float elapsed;
            public int tick;
            public bool stopWhenDone;
        }

        private sealed class Channel
        {
            private int cursor;

            public VNAudioBus bus;
            public GameObject root;
            public Slot[] slots;
            public bool crossfade;
            public string requestedCue;

            public VNAudioBus Bus => bus;
            public GameObject Root => root;
            public Slot[] Slots => slots;
            public bool Crossfade => crossfade;
            public string RequestedCue { get => requestedCue; set => requestedCue = value; }

            public int FindPlaying(string cueId)
            {
                for (var i = 0; i < slots.Length; i++)
                {
                    if (string.Equals(slots[i].cue, cueId, StringComparison.Ordinal) && slots[i].gain > 0.0005f)
                        return i;
                }
                return -1;
            }

            /// <summary>Slot that is not the loudest one, so a crossfade always has a pair.</summary>
            public int PickQuietSlot()
            {
                var loudest = 0;
                for (var i = 1; i < slots.Length; i++)
                    if (slots[i].gain > slots[loudest].gain) loudest = i;

                for (var i = 0; i < slots.Length; i++)
                {
                    if (i == loudest) continue;
                    if (slots[i].gain <= 0.0005f) return i;
                }
                return (loudest + 1) % slots.Length;
            }

            /// <summary>First silent slot, or the least recently used one when the pool is busy.</summary>
            public int PickFreeSlot()
            {
                for (var n = 0; n < slots.Length; n++)
                {
                    var i = (cursor + n) % slots.Length;
                    if (slots[i].gain <= 0.0005f && (slots[i].source == null || !slots[i].source.isPlaying))
                    {
                        cursor = (i + 1) % slots.Length;
                        return i;
                    }
                }
                var fallback = cursor % slots.Length;
                cursor = (fallback + 1) % slots.Length;
                return fallback;
            }
        }
    }
}
