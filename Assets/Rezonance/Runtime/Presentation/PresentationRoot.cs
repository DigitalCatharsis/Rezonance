// REZONANCE VN ENGINE - composition root of the presentation module.
// Finds the Canvas layers by name and hands each director its target rect. It never creates a Canvas
// and holds no dialogue logic: the engine drives the directors through the Core contracts.
using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Rezonance.VN.Core;
using UnityEngine;

namespace Rezonance.VN.Presentation
{
    /// <summary>Monobehaviour that binds the presentation directors to the VNCanvas layers.</summary>
    public sealed class PresentationRoot : MonoBehaviour
    {
        /// <summary>Name of the Canvas the UI module creates (ARCHITECTURE.md 9.6).</summary>
        public const string CanvasName = "VNCanvas";

        /// <summary>Name of the GameObject this component lives on.</summary>
        public const string RootName = "VN_Presentation";

        private const string LogArea = "presentation";

        private static PresentationRoot current;

        private readonly List<IDisposable> disposable = new List<IDisposable>(8);

        /// <summary>Background layer director.</summary>
        public BackdropDirector Backdrop { get; private set; }

        /// <summary>Portrait layer director.</summary>
        public PortraitDirector Portraits { get; private set; }

        /// <summary>Audio bus director.</summary>
        public AudioDirector Audio { get; private set; }

        /// <summary>Diegetic screen text presenter.</summary>
        public ScreenTextPresenter ScreenText { get; private set; }

        /// <summary>Mini-game stub host.</summary>
        public MiniGameStubHost MiniGames { get; private set; }

        /// <summary>Delivery to emotion mapper used by the engine when it builds a PortraitRef.</summary>
        public PortraitEmotionResolver Emotions { get; private set; }

        /// <summary>Asset cache shared by every director.</summary>
        public AssetLibrary Assets { get; private set; }

        /// <summary>Animation ticker.</summary>
        public PresentationHost Host { get; private set; }

        /// <summary>Staging overlay handed in by the orchestrator.</summary>
        public VNStageOverlay Overlay { get; private set; }

        /// <summary>Speaker map handed in by the orchestrator.</summary>
        public VNSpeakerMap SpeakerMap { get; private set; }

        /// <summary>Settings store the audio levels follow.</summary>
        public ISettingsStore Settings { get; private set; }

        /// <summary>True when at least one layer was found and bound.</summary>
        public bool IsBound { get; private set; }

        /// <summary>Live instance created by Create, null before that.</summary>
        public static PresentationRoot Instance => current;

        private void Awake()
        {
            if (current != null && current != this && current.gameObject != null)
                VNLog.Warn(LogArea, "a second " + RootName + " is alive; the orchestrator should reuse PresentationRoot.Create");
            current = this;
        }

        /// <summary>
        /// Builds the presentation objects and binds every director to its layer.
        /// Safe to call twice: the second call rebinds the existing root.
        /// </summary>
        public static PresentationRoot Create(VNStageOverlay overlay, VNSpeakerMap map, ISettingsStore settings)
        {
            var root = current != null && current.gameObject != null
                ? current
                : new GameObject(RootName).AddComponent<PresentationRoot>();

            if (root == null)
            {
                VNLog.Error(LogArea, "could not create " + RootName);
                return null;
            }

            root.Bind(overlay, map, settings);
            return root;
        }

        /// <summary>Assigns the data sources and binds the layers. Idempotent.</summary>
        public void Bind(VNStageOverlay overlay, VNSpeakerMap map, ISettingsStore settings)
        {
            Overlay = overlay;
            SpeakerMap = map;
            Settings = settings;

            if (Backdrop != null)
            {
                RebindLayers();
                return;
            }

            Host = PresentationHost.EnsureExists();
            Assets = AssetLibrary.Shared;
            Assets.CollectHintsFrom(overlay);
            Assets.CollectHintsFrom(map);

            Backdrop = new BackdropDirector(Assets, Host);
            Portraits = new PortraitDirector(Assets, Host);
            ScreenText = new ScreenTextPresenter(Host);
            MiniGames = new MiniGameStubHost(null, Assets, Host);
            Audio = new AudioDirector(Assets, Host);
            Emotions = new PortraitEmotionResolver(map);

            disposable.Add(Backdrop);
            disposable.Add(Portraits);
            disposable.Add(ScreenText);
            disposable.Add(MiniGames);
            disposable.Add(Audio);

            RebindLayers();
            Audio.BindSettings(Settings);
        }

        /// <summary>
        /// Re-reads the Canvas layers and rebinds the directors. Call after the UI module rebuilds the Canvas.
        /// </summary>
        public void RebindLayers()
        {
            if (Backdrop == null) return;

            var backdropLayer = FindLayer(BackdropDirector.LayerName);
            var portraitLayer = FindLayer(PortraitDirector.LayerName);
            var screenLayer = FindLayer(ScreenTextPresenter.LayerName);
            var modalLayer = FindLayer(MiniGameStubHost.LayerName);

            Backdrop.Init(backdropLayer);
            Portraits.Init(portraitLayer);
            ScreenText.Init(screenLayer);
            MiniGames.Init(modalLayer);
            Audio.Init(transform);

            IsBound = backdropLayer != null || portraitLayer != null || screenLayer != null || modalLayer != null;
            if (!IsBound)
                VNLog.Warn(LogArea, "no " + CanvasName + " layer was found, presentation runs inert");
            else
                VNLog.Info(LogArea, "presentation bound to " + CanvasName);
        }

        /// <summary>Attaches the localizer used by the mini-game stub panel.</summary>
        public void AttachLocalizer(Func<string, string> localize)
        {
            if (MiniGames != null) MiniGames.SetLocalizer(localize);
        }

        /// <summary>Fans one cue set out to the directors. Pure routing, no dialogue decisions.</summary>
        public void ApplyCues(VNStageCues cues)
        {
            if (cues == null) return;

            if (!string.IsNullOrEmpty(cues.backdropKey))
                Backdrop.Show(cues.backdropKey, cues.fade, cues.fadeSeconds);

            if (cues.hideAllPortraits) Portraits.HideAll(cues.fadeSeconds);

            if (cues.portraits != null)
            {
                for (var i = 0; i < cues.portraits.Length; i++)
                {
                    var portrait = cues.portraits[i];
                    if (!portrait.IsValid) continue;
                    Portraits.Show(portrait, cues.fadeSeconds);
                }
            }

            if (cues.audio != null)
            {
                for (var i = 0; i < cues.audio.Length; i++)
                {
                    var cue = cues.audio[i];
                    if (string.IsNullOrEmpty(cue.cueId)) continue;
                    if (cue.stop) Audio.Stop(cue.bus, cue.fadeSeconds);
                    else Audio.Play(cue.bus, cue.cueId, cue.fadeSeconds, LoopByDefault(cue.bus));
                }
            }

            if (!string.IsNullOrEmpty(cues.screenText))
                ScreenText.Show(cues.screenText, cues.holdSeconds);
        }

        /// <summary>Backdrop key authored for a node, or null. Thin wrapper over the overlay contract.</summary>
        public string BackdropKeyFor(VNNode node)
        {
            if (node == null || Overlay == null) return null;
            var cues = Overlay.ForNode(node);
            return string.IsNullOrEmpty(cues.backdropKey) ? node.backdropHint : cues.backdropKey;
        }

        /// <summary>Portrait reference for a node using the speaker map and the delivery brief.</summary>
        public PortraitRef? PortraitFor(VNNode node, string overlayEmotion = null, PortraitSlot? forcedSlot = null)
        {
            if (node == null || Emotions == null) return null;
            return Emotions.ResolvePortrait(node.speaker, node.delivery, overlayEmotion, forcedSlot);
        }

        /// <summary>Stops every animation and clears the screen, for example before loading a save.</summary>
        public void ResetAll()
        {
            Backdrop?.Reset();
            Portraits?.Reset();
            ScreenText?.Hide();
            MiniGames?.Reset();
            Audio?.Reset();
        }

        /// <summary>Warms every asset the overlay and the speaker map mention.</summary>
        public UniTask PreloadAssetsAsync(IProgress<float> progress = null)
        {
            if (Assets == null || Host == null) return UniTask.CompletedTask;
            return Assets.PreloadAllAsync(progress, Host.Token);
        }

        /// <summary>True when music or ambience should loop unless the cue says otherwise.</summary>
        public static bool LoopByDefault(VNAudioBus bus)
        {
            return bus == VNAudioBus.Music || bus == VNAudioBus.Ambience;
        }

        /// <summary>Finds a layer of the VNCanvas by direct child name; null when the Canvas is missing.</summary>
        public static RectTransform FindLayer(string layerName)
        {
            var canvas = GameObject.Find(CanvasName);
            if (canvas == null) return null;
            return FindUnder(canvas.transform, layerName);
        }

        /// <summary>Finds a layer under an explicit Canvas transform, the robust variant of FindLayer.</summary>
        public static RectTransform FindUnder(Transform canvasTransform, string layerName)
        {
            if (canvasTransform == null) return null;
            var child = canvasTransform.Find(layerName);
            if (child == null) return null;
            return child as RectTransform;
        }

        private void OnDestroy()
        {
            if (Audio != null) Audio.UnbindSettings();
            for (var i = disposable.Count - 1; i >= 0; i--)
            {
                try
                {
                    disposable[i]?.Dispose();
                }
                catch (Exception ex)
                {
                    VNLog.Error(LogArea, "dispose failed: " + ex.Message);
                }
            }
            disposable.Clear();

            Backdrop = null;
            Portraits = null;
            ScreenText = null;
            MiniGames = null;
            Audio = null;
            Emotions = null;
            IsBound = false;
            if (current == this) current = null;
        }
    }
}
