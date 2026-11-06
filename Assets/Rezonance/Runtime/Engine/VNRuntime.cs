// REZONANCE VN ENGINE - the dialogue state machine.
// Owns branch traversal (ARCHITECTURE.md 5), conditional gating (9.5), menus with return to the
// mainline, run modes, seen/flag ledgers, backlog, autosave points and quick slots. It talks to the
// world only through Runtime/Core contracts, so it is testable without a scene.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Cysharp.Threading.Tasks;
using Rezonance.VN.Core;
using TMPro;
using UnityEngine;

namespace Rezonance.VN.Engine
{
    /// <summary>Localization keys and tunables used by the engine module only.</summary>
    public static class VNDefaults
    {
        // Interface keys (ARCHITECTURE.md 9.8); the engine never prints a literal sentence.
        public const string KeyChoicesHint = "ui.game.choices_hint";
        public const string KeyMinigamePlaceholder = "ui.game.minigame_placeholder";
        public const string KeySpeakerPlayer = "ui.speaker.player";
        public const string KeySpeakerInnerVoice = "ui.speaker.inner_voice";
        public const string KeySpeakerSystem = "ui.speaker.system";

        // Flag name prefixes written by the engine and readable from conditionExpr.
        public const string ChosenFlagPrefix = "chosen.";
        public const string SeenFlagPrefix = "seen.";
        public const string MenuFlagPrefix = "menu.";
        public const string SceneFlagPrefix = "scene.";
        public const string ChapterFlagPrefix = "chapter.";
        public const string MiniGameFlagPrefix = "minigame.";

        // Pacing and ranges.
        public const float PunctuationHoldScale = 0.25f;
        public const float FastMultiplier = 4f;
        public const float AutoDelayMin = 0.4f;
        public const float AutoDelayMax = 4f;
        public const float ReadDelayMin = 0.05f;
        public const float ReadDelayMax = 1f;
        public const float SkipStepSeconds = 0.02f;
        public const float TextCpsMax = 120f;
        public const int BacklogMin = 16;
        public const int BacklogMax = 2000;
        public const int TraversalCap = 20000;
    }

    /// <summary>Implements <see cref="IVNRuntime"/> over a <see cref="VNDataRegistry"/>.</summary>
    public sealed class VNRuntime : IVNRuntime, IConditionSource, IDisposable
    {
        private readonly VNRuntimeDependencies deps;
        private readonly string logArea;
        private readonly FlagLedger flags = new FlagLedger();
        private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> seenOrder = new List<string>();
        private readonly List<string> resumeStack = new List<string>();
        private readonly HashSet<string> warnedKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly ConditionEvaluator evaluator = ConditionEvaluator.Shared;
        private readonly Backlog history;
        private readonly VNRuntimeClock clock;
        private readonly VNRenderContext context = new VNRenderContext();

        private VNRunMode mode = VNRunMode.Manual;
        private VNBranchDef currentBranch;
        private VNSceneDef currentScene;
        private VNNode currentNode;
        private VNStageCues currentCues = VNStageCues.Empty;
        private string currentText = string.Empty;
        private int nodeIndexInBranch;
        private bool choicePending;
        private bool typing;
        private bool typingResolved;
        private bool miniGamePending;
        private bool finished;
        private bool disposed;
        private bool suppressBacklog;
        private bool autoWireInput = true;
        private int generation;
        private CancellationTokenSource pendingCts;
        private CancellationTokenSource typingCts;
        private string lastAutosaveKey;

        /// <inheritdoc/>
        public event Action<VNRenderContext> NodeChanged;

        /// <inheritdoc/>
        public event Action<VNNode> MenuOpened;

        /// <inheritdoc/>
        public event Action MenuClosed;

        /// <inheritdoc/>
        public event Action<VNBranchDef> BranchChanged;

        /// <inheritdoc/>
        public event Action<VNSceneDef> SceneChanged;

        /// <inheritdoc/>
        public event Action Finished;

        /// <inheritdoc/>
        public event Action<string> MiniGameRequested;

        /// <summary>Creates the machine. Only Registry is required; anything else degrades to a warning.</summary>
        public VNRuntime(VNRuntimeDependencies deps)
        {
            this.deps = deps ?? VNRuntimeDependencies.CreateEmpty();
            logArea = string.IsNullOrEmpty(this.deps.LogArea) ? "Engine" : this.deps.LogArea;
            history = new Backlog(SettingsNow().backlogMaxEntries);
            clock = this.deps.Clock ?? new VNRuntimeClock(this.deps);
            this.deps.Clock = clock;

            var missing = this.deps.Missing();
            if (missing.Length > 0) VNLog.Warn(logArea, "dependencies not supplied: " + missing);

            if (this.deps.Settings != null)
            {
                this.deps.Settings.Changed += OnSettingsChanged;
                ApplyVolumes(SettingsNow());
            }
            if (this.deps.Localizer != null) this.deps.Localizer.LanguageChanged += OnLanguageChanged;
            if (this.deps.Input != null)
            {
                this.deps.Input.Command += OnInputCommand;
                this.deps.Input.ChoiceSelected += OnInputChoice;
                this.deps.Input.Enable();
            }
        }

        /// <summary>
        /// TMP component the typewriter reveals into. The view usually animates its own label; binding it
        /// here lets the engine drive the reveal directly. Null = timing only, no per-character painting.
        /// </summary>
        public TMP_Text TargetText { get; set; }

        /// <summary>
        /// True (default) means the runtime listens to <see cref="IVNInput"/> and maps commands itself.
        /// Set false when the UI prefers to call Advance/Choose from its own handlers, to avoid double advance.
        /// </summary>
        public bool AutoWireInput
        {
            get => autoWireInput;
            set
            {
                if (autoWireInput == value || deps.Input == null) return;
                autoWireInput = value;
                if (value)
                {
                    deps.Input.Command += OnInputCommand;
                    deps.Input.ChoiceSelected += OnInputChoice;
                }
                else
                {
                    deps.Input.Command -= OnInputCommand;
                    deps.Input.ChoiceSelected -= OnInputChoice;
                }
            }
        }

        /// <summary>Typed flag store; conditions read it through this runtime, tools and tests may peek.</summary>
        public FlagLedger Flags => flags;

        /// <summary>Ring buffer of shown lines; <see cref="IVNRuntime.Backlog"/> is its read-only face.</summary>
        public Backlog History => history;

        /// <summary>Session playtime, paused while a menu or a mini-game owns the screen.</summary>
        public VNRuntimeClock Clock => clock;

        /// <summary>Condition evaluator used by this runtime (shared cache).</summary>
        public ConditionEvaluator Evaluator => evaluator;

        /// <summary>Branch a fork returns to, innermost of the stack; null on the mainline.</summary>
        public string ResumeBranchId => resumeStack.Count > 0 ? resumeStack[resumeStack.Count - 1] : null;

        /// <summary>How many distinct nodes the player has already seen.</summary>
        public int SeenCount => seen.Count;

        /// <summary>True when the node id was shown at least once.</summary>
        public bool IsSeen(string nodeId) => !string.IsNullOrEmpty(nodeId) && seen.Contains(nodeId);

        /// <summary>Text currently on screen, already localized.</summary>
        public string CurrentText => currentText;

        // ------------------------------------------------------------------ IVNRuntime surface

        /// <inheritdoc/>
        public VNRunMode Mode => mode;

        /// <inheritdoc/>
        public VNNode CurrentNode => currentNode;

        /// <inheritdoc/>
        public VNBranchDef CurrentBranch => currentBranch;

        /// <inheritdoc/>
        public VNSceneDef CurrentScene => currentScene;

        /// <inheritdoc/>
        public bool ChoicePending => choicePending;

        /// <inheritdoc/>
        public bool IsTyping => typing;

        /// <inheritdoc/>
        public bool AtEndOfAct => finished;

        /// <inheritdoc/>
        public IReadOnlyList<VNBacklogEntry> Backlog => history.Entries;

        /// <inheritdoc/>
        public void StartAct(string sceneId = null)
        {
            var registry = deps.Registry;
            if (registry == null)
            {
                VNLog.Error(logArea, "StartAct: no VNDataRegistry - the game cannot start");
                return;
            }
            if (registry.Scenes.Count == 0)
            {
                VNLog.Error(logArea, "StartAct: registry has no scenes");
                return;
            }

            finished = false;
            lastAutosaveKey = null;
            seen.Clear();
            seenOrder.Clear();
            flags.Clear();
            history.Clear();
            resumeStack.Clear();
            CloseMenuUi();
            CancelPendingTimers();
            nodeIndexInBranch = 0;
            currentNode = null;

            VNSceneDef scene;
            if (string.IsNullOrEmpty(sceneId)) scene = registry.Scenes[0];
            else
            {
                scene = registry.GetScene(sceneId);
                if (scene == null)
                {
                    VNLog.Error(logArea, "StartAct: unknown scene id '" + sceneId + "'");
                    return;
                }
            }

            currentScene = scene;
            SceneChanged?.Invoke(scene);

            var branch = scene.branches != null && scene.branches.Count > 0 ? scene.branches[0] : null;
            if (branch == null)
            {
                VNLog.Warn(logArea, "scene '" + scene.id + "' has no branches, the act ends here");
                FinishAct();
                return;
            }
            EnterBranch(branch, 0);
        }

        /// <inheritdoc/>
        public bool StartBranch(string branchId, string resumeBranchId = null)
        {
            var registry = deps.Registry;
            if (registry == null)
            {
                VNLog.Error(logArea, "StartBranch: no registry");
                return false;
            }
            var branch = registry.GetBranch(branchId);
            if (branch == null)
            {
                VNLog.Warn(logArea, "StartBranch: unknown branch '" + branchId + "'");
                return false;
            }

            finished = false;
            CancelPendingTimers();
            CloseMenuUi();
            resumeStack.Clear();
            if (!string.IsNullOrEmpty(resumeBranchId) && !string.Equals(resumeBranchId, branchId, StringComparison.Ordinal))
                resumeStack.Add(resumeBranchId);

            EnterBranch(branch, 0);
            return currentBranch != null;
        }

        /// <inheritdoc/>
        public bool JumpToNode(string nodeId)
        {
            var registry = deps.Registry;
            if (registry == null) return false;
            var node = registry.GetNode(nodeId);
            if (node == null)
            {
                VNLog.Warn(logArea, "JumpToNode: unknown node '" + nodeId + "'");
                return false;
            }
            var branch = FindBranchOf(registry, node, nodeId);
            if (branch == null)
            {
                VNLog.Warn(logArea, "JumpToNode: node '" + nodeId + "' is not indexed in any branch");
                return false;
            }

            finished = false;
            CancelPendingTimers();
            CloseMenuUi();
            var index = IndexOf(branch, nodeId);
            EnterBranch(branch, index < 0 ? 0 : index);
            return currentNode != null;
        }

        /// <inheritdoc/>
        public void Advance()
        {
            if (finished) return;
            if (deps.Registry == null || currentBranch == null)
            {
                WarnOnce("advance-no-start", "Advance before StartAct/RestoreState does nothing");
                return;
            }
            if (deps.Input != null && deps.Input.CaptureUiOnly) return;
            if (miniGamePending)
            {
                WarnOnce("advance-minigame", "Advance ignored: a mini-game owns the screen");
                return;
            }
            if (choicePending)
            {
                WarnOnce("advance-menu", "Advance ignored: a choice is pending, call Choose");
                return;
            }
            if (typing)
            {
                CompleteTyping();
                return;
            }
            AdvanceCurrent();
        }

        /// <inheritdoc/>
        public void Choose(int optionIndex)
        {
            var menu = currentNode;
            if (!choicePending || menu == null || menu.kind != VNNodeKind.Menu || menu.options == null)
            {
                WarnOnce("choose-no-menu", "Choose(" + optionIndex + ") with no menu open");
                return;
            }
            if (optionIndex < 0 || optionIndex >= menu.options.Count)
            {
                VNLog.Warn(logArea, "Choose: index " + optionIndex + " out of range (0.." + (menu.options.Count - 1) + ")");
                return;
            }

            var option = menu.options[optionIndex];
            if (option == null)
            {
                VNLog.Warn(logArea, "Choose: null option " + optionIndex + " in menu '" + menu.id + "'");
                return;
            }
            var registry = deps.Registry;
            if (string.IsNullOrEmpty(option.targetBranchId))
            {
                VNLog.Warn(logArea, "menu option '" + (option.id ?? optionIndex.ToString(CultureInfo.InvariantCulture)) +
                                    "' has no target branch, the importer left it unresolved");
                return;
            }
            var target = registry != null ? registry.GetBranch(option.targetBranchId) : null;
            if (target == null)
            {
                VNLog.Warn(logArea, "menu option '" + option.id + "' points at missing branch '" + option.targetBranchId + "'");
                return;
            }

            flags.RecordChoice(new VNChoiceEntry
            {
                menuId = menu.id,
                optionIndex = optionIndex,
                optionId = option.id,
                targetBranchId = option.targetBranchId
            });

            // Where the fork must return to: the importer writes it on the menu node, next branch is the fallback.
            var resume = menu.resumeBranchId;
            if (string.IsNullOrEmpty(resume))
            {
                var following = registry != null ? registry.NextBranch(currentBranch) : null;
                resume = following != null ? following.id : null;
            }
            if (!string.IsNullOrEmpty(resume) && !string.Equals(resume, option.targetBranchId, StringComparison.Ordinal))
            {
                resumeStack.Add(resume);
                flags.SetText(VNDefaults.MenuFlagPrefix + menu.id + ".resume", resume);
            }

            CloseMenuUi();
            EnterBranch(target, 0);
        }

        /// <inheritdoc/>
        public void SetMode(VNRunMode runMode)
        {
            var previous = mode;
            mode = runMode;
            deps.View?.SetRunMode(mode);

            if (runMode == VNRunMode.Fast && typing) CompleteTyping();

            if (runMode == VNRunMode.SkipUnseen && previous != runMode &&
                !choicePending && !miniGamePending && !typing && !finished)
            {
                AdvanceCurrent();
            }
            else if (runMode == VNRunMode.Auto && !typing && !choicePending && !miniGamePending && !finished)
            {
                ScheduleContinuation();
            }
        }

        /// <inheritdoc/>
        public void CompleteTyping()
        {
            if (!typing) return;
            typing = false;
            if (typingCts != null && !typingCts.IsCancellationRequested) typingCts.Cancel();
            if (TargetText != null) Typewriter.Finish(TargetText, currentText);
            ResolveTyping();
            ScheduleContinuation();
        }

        /// <inheritdoc/>
        public VNGameState CaptureState()
        {
            var state = new VNGameState
            {
                formatVersion = VNDataFormat.Version,
                actId = deps.Registry != null && deps.Registry.Data != null ? deps.Registry.Data.actId : null,
                sceneId = currentScene != null ? currentScene.id : null,
                branchId = currentBranch != null ? currentBranch.id : null,
                nodeId = currentNode != null ? currentNode.id : null,
                nodeIndexInBranch = nodeIndexInBranch,
                resumeBranchId = ResumeBranchId,
                language = LanguageNow(),
                playtimeSeconds = clock.PlaytimeSeconds,
                savedAtUtcTicks = DateTime.UtcNow.Ticks
            };
            for (var i = 0; i < seenOrder.Count; i++) state.seenNodeIds.Add(seenOrder[i]);
            state.flags = flags.ToEntries();
            for (var i = 0; i < flags.Choices.Count; i++) state.choices.Add(flags.Choices[i]);
            state.backlogNodeIds = history.NodeIds();
            return state;
        }

        /// <inheritdoc/>
        public bool RestoreState(VNGameState state)
        {
            if (state == null)
            {
                VNLog.Warn(logArea, "RestoreState: null state");
                return false;
            }
            var registry = deps.Registry;
            if (registry == null)
            {
                VNLog.Error(logArea, "RestoreState: no registry loaded");
                return false;
            }
            if (state.formatVersion > VNDataFormat.Version)
                VNLog.Warn(logArea, "RestoreState: save format " + state.formatVersion + " is newer than " + VNDataFormat.Version);

            CancelPendingTimers();
            CloseMenuUi();
            flags.Clear();
            seen.Clear();
            seenOrder.Clear();
            resumeStack.Clear();
            history.Clear();
            finished = false;
            currentNode = null;
            nodeIndexInBranch = 0;

            flags.FromEntries(state.flags);
            flags.FromChoices(state.choices);
            for (var i = 0; i < state.seenNodeIds.Count; i++) MarkSeenRaw(state.seenNodeIds[i]);

            if (deps.Localizer != null && state.language != deps.Localizer.Language) deps.Localizer.SetLanguage(state.language);
            clock.Set(state.playtimeSeconds);

            var branch = ResolveBranch(registry, state);
            if (branch == null)
            {
                VNLog.Warn(logArea, "RestoreState: branch '" + (state.branchId ?? "<null>") + "' is gone, falling back to the act start");
                mode = VNRunMode.Manual;
                deps.View?.SetRunMode(mode);
                StartAct(state.sceneId);
                return false;
            }

            if (!string.IsNullOrEmpty(state.resumeBranchId) &&
                !string.Equals(state.resumeBranchId, branch.id, StringComparison.Ordinal))
                resumeStack.Add(state.resumeBranchId);

            SetMode(VNRunMode.Manual);

            // The restored node is painted but must not duplicate the backlog rows coming from the save.
            suppressBacklog = true;
            EnterBranch(branch, IndexOf(branch, state.nodeId) < 0 ? Clamp(state.nodeIndexInBranch, 0, Math.Max(0, branch.Count - 1)) : IndexOf(branch, state.nodeId));
            suppressBacklog = false;

            if (state.backlogNodeIds != null && state.backlogNodeIds.Count > 0)
                history.Restore(state.backlogNodeIds, RebuildEntry);

            VNLog.Info(logArea, "restored at " + (currentNode != null ? currentNode.id : branch.id) + ", seen " + seen.Count);
            return currentNode != null;
        }

        // ------------------------------------------------------------------ traversal

        /// <summary>Moves one step forward from the current node, honouring gates, modes and branch ends.</summary>
        private void AdvanceCurrent()
        {
            Traverse(true);
        }

        private void Traverse(bool advanceFirst)
        {
            if (deps.Registry == null) return;
            var guard = 0;
            while (guard++ < VNDefaults.TraversalCap)
            {
                if (finished) return;

                if (currentBranch == null)
                {
                    if (!AdvanceToFollowingBranch()) return;
                    continue;
                }

                if (advanceFirst && !MoveNext()) return;
                advanceFirst = true;

                var nodes = currentBranch.nodes;
                if (nodes == null || nodeIndexInBranch >= nodes.Count)
                {
                    if (!AdvanceToFollowingBranch()) return;
                    continue;
                }

                var node = nodes[nodeIndexInBranch];
                if (node == null) continue;
                if (!PassesGate(node)) continue;
                if (IsSkippedByMode(node)) continue;
                if (IsAutoSkippable(node)) continue;

                Show(node);
                return;
            }
            VNLog.Warn(logArea, "traversal hit the " + VNDefaults.TraversalCap + " node guard, stopping to stay responsive");
        }

        private bool MoveNext()
        {
            nodeIndexInBranch++;
            var nodes = currentBranch != null ? currentBranch.nodes : null;
            if (nodes == null || nodeIndexInBranch >= nodes.Count) return AdvanceToFollowingBranch();
            return true;
        }

        private bool AdvanceToFollowingBranch()
        {
            var registry = deps.Registry;

            if (resumeStack.Count > 0)
            {
                var resumeId = resumeStack[resumeStack.Count - 1];
                resumeStack.RemoveAt(resumeStack.Count - 1);
                var resume = registry != null ? registry.GetBranch(resumeId) : null;
                if (resume != null)
                {
                    SetBranch(resume, 0);
                    return true;
                }
                VNLog.Warn(logArea, "resume branch '" + resumeId + "' is missing, continuing on the mainline");
            }

            var next = registry != null ? registry.NextBranch(currentBranch) : null;
            if (next != null)
            {
                SetBranch(next, 0);
                return true;
            }

            var crossScene = registry != null ? registry.FirstBranchOfNextScene(currentScene) : null;
            if (crossScene != null)
            {
                SetBranch(crossScene, 0);
                return true;
            }

            FinishAct();
            return false;
        }

        private void EnterBranch(VNBranchDef branch, int index)
        {
            if (SetBranch(branch, index)) Traverse(false);
        }

        /// <summary>Points the machine at a branch and announces it; no traversal happens here.</summary>
        private bool SetBranch(VNBranchDef branch, int index)
        {
            if (branch == null)
            {
                FinishAct();
                return false;
            }

            currentBranch = branch;
            nodeIndexInBranch = Clamp(index, 0, Math.Max(0, branch.Count));

            var registry = deps.Registry;
            var scene = registry != null ? registry.GetScene(branch.sceneId) : null;
            if (scene != null && (currentScene == null || !string.Equals(scene.id, currentScene.id, StringComparison.Ordinal)))
            {
                currentScene = scene;
                SceneChanged?.Invoke(scene);
                Autosave(true);
            }

            BranchChanged?.Invoke(branch);
            flags.Inc(VNDefaults.ChapterFlagPrefix + "visited", 1L);
            if (!string.IsNullOrEmpty(branch.sceneId)) flags.Set(VNDefaults.SceneFlagPrefix + branch.sceneId, true);
            if (!string.IsNullOrEmpty(branch.id)) flags.Set(VNDefaults.ChapterFlagPrefix + branch.id, true);
            Autosave(false);
            return true;
        }

        private void FinishAct()
        {
            if (finished) return;
            finished = true;
            CancelPendingTimers();
            CloseMenuUi();
            typing = false;
            deps.View?.NotifyBusy(false);
            if (deps.Input != null) deps.Input.ChoiceCount = 0;
            Finished?.Invoke();
            VNLog.Info(logArea, currentNode != null ? "act finished after " + currentNode.id : "act finished");
        }

        // ------------------------------------------------------------------ one node

        private void Show(VNNode node)
        {
            currentNode = node;
            miniGamePending = false;
            typingResolved = false;
            BeginGeneration();

            var cues = CuesFor(node);
            currentCues = cues;
            var text = LocalizedText(node);
            currentText = text;

            MarkSeen(node);

            // Cues reach presentation first, the view sees the node afterwards (section 5).
            ApplyPresentation(node, cues, text);

            if (!suppressBacklog) history.Add(node, ResolveSpeaker(node), text);
            EmitNode(node, cues, text);

            if (node.kind == VNNodeKind.MiniGame)
            {
                RunMiniGame(node, cues);
                return;
            }

            if (node.kind == VNNodeKind.Menu) OpenMenu(node);
            StartTyping(text, cues);
        }

        private VNStageCues CuesFor(VNNode node)
        {
            var overlay = deps.Overlay;
            if (overlay == null) return VNStageCues.Empty;
            var cues = overlay.ForNode(node);
            return cues ?? VNStageCues.Empty;
        }

        private void ApplyPresentation(VNNode node, VNStageCues cues, string text)
        {
            var settings = SettingsNow();

            var backdrop = deps.Backdrop;
            if (backdrop != null)
            {
                var key = !string.IsNullOrEmpty(cues.backdropKey) ? cues.backdropKey : node.backdropHint;
                if (!string.IsNullOrEmpty(key) && !string.Equals(key, backdrop.CurrentKey, StringComparison.Ordinal))
                    backdrop.Show(key, cues.fade, cues.fadeSeconds);
            }

            var screenText = deps.ScreenText;
            if (screenText != null)
            {
                if (!string.IsNullOrEmpty(cues.screenText)) screenText.Show(cues.screenText, ScreenTextSeconds(cues));
                else if (node.kind == VNNodeKind.ScreenText && !string.IsNullOrEmpty(text)) screenText.Show(text, ScreenTextSeconds(cues));
                else screenText.Hide();
            }

            ApplyAudio(cues);
            ApplyPortraits(node, cues, settings);
        }

        private void ApplyAudio(VNStageCues cues)
        {
            var audio = deps.Audio;
            if (audio == null || cues.audio == null) return;
            for (var i = 0; i < cues.audio.Length; i++)
            {
                var cue = cues.audio[i];
                if (cue.stop || string.IsNullOrEmpty(cue.cueId))
                {
                    audio.Stop(cue.bus, cue.fadeSeconds);
                    continue;
                }
                // Naming convention: sfx_* is a one-shot, music and ambience loop.
                var loop = !cue.cueId.StartsWith("sfx_", StringComparison.Ordinal);
                audio.Play(cue.bus, cue.cueId, cue.fadeSeconds, loop);
            }
        }

        private void ApplyPortraits(VNNode node, VNStageCues cues, VNSettings settings)
        {
            var portraits = deps.Portraits;
            if (portraits == null) return;
            if (cues.hideAllPortraits) portraits.HideAll(cues.fadeSeconds);
            var refs = cues.portraits;
            if (refs == null || refs.Length == 0) return;

            var speakingSlot = PortraitSlot.None;
            for (var i = 0; i < refs.Length; i++)
            {
                var p = refs[i];
                if (!p.IsValid) continue;
                portraits.Show(p, cues.fadeSeconds);
                if (SpeakingMatch(node, p)) speakingSlot = p.slot;
            }
            if (speakingSlot != PortraitSlot.None)
                portraits.SetFocus(speakingSlot, Clamp(settings.portraitDimming, 0f, 1f));
        }

        /// <summary>Best-effort "who is talking" test between a speaker label and a portrait asset key.</summary>
        private static bool SpeakingMatch(VNNode node, PortraitRef portrait)
        {
            if (string.IsNullOrEmpty(node.speaker) || string.IsNullOrEmpty(portrait.characterId)) return false;
            var slug = VNSlug.Of(node.speaker);
            return slug.Length > 0 && portrait.characterId.IndexOf(slug, StringComparison.Ordinal) >= 0;
        }

        private void EmitNode(VNNode node, VNStageCues cues, string text)
        {
            context.node = node;
            context.text = text;
            context.speakerName = ResolveSpeaker(node);
            context.role = node.role;
            context.delivery = node.delivery;
            context.choicePending = node.kind == VNNodeKind.Menu;
            context.mode = mode;
            context.cues = cues;

            NodeChanged?.Invoke(context);
            deps.View?.Render(context);
        }

        private void OpenMenu(VNNode menu)
        {
            choicePending = true;
            clock.SetPaused(VNRuntimeClock.PauseSource.Menu, true);

            var options = menu.options;
            var count = options != null ? options.Count : 0;
            var labels = new List<string>(count);
            var enabled = new List<bool>(count);
            for (var i = 0; i < count; i++)
            {
                var option = options[i];
                labels.Add(OptionText(menu, i, option));
                enabled.Add(OptionAvailable(option));
            }

            if (deps.Input != null) deps.Input.ChoiceCount = count;
            deps.View?.ShowChoices(menu, labels, enabled);
            MenuOpened?.Invoke(menu);
            Autosave(true);
        }

        private void CloseMenuUi()
        {
            if (deps.Input != null) deps.Input.ChoiceCount = 0;
            if (!choicePending) return;
            choicePending = false;
            clock.SetPaused(VNRuntimeClock.PauseSource.Menu, false);
            deps.View?.HideChoices();
            MenuClosed?.Invoke();
        }

        private bool OptionAvailable(VNMenuOption option)
        {
            if (option == null || string.IsNullOrEmpty(option.targetBranchId)) return false;
            var registry = deps.Registry;
            if (registry == null) return true;
            return registry.GetBranch(option.targetBranchId) != null;
        }

        // ------------------------------------------------------------------ typing

        private void StartTyping(string text, VNStageCues cues)
        {
            var cps = CpsFor(cues);
            if (cps <= 0f || string.IsNullOrEmpty(text) || mode == VNRunMode.Fast)
            {
                typing = false;
                ResolveTyping();
                ScheduleContinuation();
                return;
            }

            typing = true;
            deps.View?.NotifyBusy(true);
            if (typingCts != null) typingCts.Cancel();
            typingCts = new CancellationTokenSource();
            PlayTyping(text, cps, typingCts.Token, generation).Forget();
        }

        private async UniTaskVoid PlayTyping(string text, float cps, CancellationToken ct, int gen)
        {
            try
            {
                await Typewriter.Play(
                    text,
                    TargetText,
                    cps,
                    Clamp(SettingsNow().autoReadDelaySeconds, VNDefaults.ReadDelayMin, VNDefaults.ReadDelayMax),
                    mode == VNRunMode.Fast ? VNDefaults.FastMultiplier : 1f,
                    0,
                    ct,
                    null);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                VNLog.Warn(logArea, "typewriter failed: " + e.Message);
            }
            if (gen != generation || disposed) return;
            typing = false;
            ResolveTyping();
            ScheduleContinuation();
        }

        private void ResolveTyping()
        {
            if (typingResolved) return;
            typingResolved = true;
            deps.View?.NotifyBusy(false);
            deps.View?.NotifyTypingFinished();
        }

        private float CpsFor(VNStageCues cues)
        {
            var cps = cues != null && cues.typingSpeed > 0f ? cues.typingSpeed : SettingsNow().textSpeedCps;
            if (float.IsNaN(cps) || float.IsInfinity(cps)) return 0f;
            return Clamp(cps, 0f, VNDefaults.TextCpsMax);
        }

        private float ScreenTextSeconds(VNStageCues cues)
        {
            if (cues != null && cues.holdSeconds > 0f) return cues.holdSeconds;
            return Clamp(SettingsNow().autoReadDelaySeconds * 6f, 1f, 8f);
        }

        // ------------------------------------------------------------------ continuation

        private void ScheduleContinuation()
        {
            if (finished || choicePending || miniGamePending) return;
            switch (mode)
            {
                case VNRunMode.Auto:
                {
                    var settings = SettingsNow();
                    var hold = currentCues != null && currentCues.holdSeconds > 0f
                        ? currentCues.holdSeconds
                        : Clamp(settings.autoReadDelaySeconds, VNDefaults.ReadDelayMin, VNDefaults.ReadDelayMax);
                    var wait = Clamp(settings.autoNodeDelaySeconds, VNDefaults.AutoDelayMin, VNDefaults.AutoDelayMax) + hold;
                    Schedule(wait);
                    break;
                }
                case VNRunMode.SkipUnseen:
                    Schedule(VNDefaults.SkipStepSeconds);
                    break;
                default:
                    break;
            }
        }

        private void Schedule(float seconds)
        {
            BeginGeneration();
            Delay(seconds, generation).Forget();
        }

        private async UniTaskVoid Delay(float seconds, int gen)
        {
            try
            {
                await UniTask.Delay(
                    TimeSpan.FromSeconds(Math.Max(0f, seconds)),
                    DelayType.UnscaledDeltaTime,
                    PlayerLoopTiming.Update,
                    pendingCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (gen != generation || disposed) return;
            if (finished || choicePending || miniGamePending) return;
            if (deps.Input != null && deps.Input.CaptureUiOnly) return;
            AdvanceCurrent();
        }

        private void BeginGeneration()
        {
            generation++;
            if (pendingCts != null) pendingCts.Cancel();
            pendingCts = new CancellationTokenSource();
        }

        private void CancelPendingTimers()
        {
            generation++;
            if (pendingCts != null) pendingCts.Cancel();
            if (typingCts != null) typingCts.Cancel();
            typing = false;
        }

        // ------------------------------------------------------------------ mini-games

        private void RunMiniGame(VNNode node, VNStageCues cues)
        {
            var id = MiniGameId(node, cues);
            if (string.IsNullOrEmpty(id))
            {
                VNLog.Warn(logArea, "mini-game node '" + node.id + "' carries no id, skipping");
                AdvanceCurrent();
                return;
            }

            var host = deps.MiniGames;
            if (host == null)
            {
                VNLog.Warn(logArea, "no IVNMiniGameHost wired, mini-game '" + id + "' skipped");
                flags.SetText(VNDefaults.MiniGameFlagPrefix + id, "skipped");
                AdvanceCurrent();
                return;
            }
            if (!host.Has(id))
            {
                VNLog.Warn(logArea, "mini-game '" + id + "' is not registered with the host, skipping");
                flags.SetText(VNDefaults.MiniGameFlagPrefix + id, "unknown");
                AdvanceCurrent();
                return;
            }

            miniGamePending = true;
            clock.SetPaused(VNRuntimeClock.PauseSource.MiniGame, true);
            MiniGameRequested?.Invoke(id);
            host.Launch(id, completed => OnMiniGameCompleted(id, completed));
        }

        private void OnMiniGameCompleted(string id, bool success)
        {
            if (!miniGamePending) return;
            miniGamePending = false;
            clock.SetPaused(VNRuntimeClock.PauseSource.MiniGame, false);
            flags.Set(VNDefaults.MiniGameFlagPrefix + id, success);
            flags.Inc(VNDefaults.MiniGameFlagPrefix + id + ".plays", 1L);
            AdvanceCurrent();
        }

        private static string MiniGameId(VNNode node, VNStageCues cues)
        {
            if (cues != null && !string.IsNullOrEmpty(cues.miniGameId)) return cues.miniGameId;
            if (node != null && !string.IsNullOrEmpty(node.targetBranchId)) return node.targetBranchId;
            return node != null ? node.id : null;
        }

        // ------------------------------------------------------------------ gates and modes

        /// <summary>9.5: a non-empty conditionExpr that evaluates false hides the node entirely.</summary>
        private bool PassesGate(VNNode node)
        {
            var expression = node.conditionExpr;
            if (string.IsNullOrEmpty(expression))
            {
                // A conditional marker with no bound expression cannot be judged: the importer lists it.
                if (node.kind == VNNodeKind.Conditional) return false;
                return true;
            }
            var result = evaluator.Evaluate(expression, this);
            return result;
        }

        /// <summary>SkipUnseen walks past every node the player has already read and stops on menus.</summary>
        private bool IsSkippedByMode(VNNode node)
        {
            if (mode != VNRunMode.SkipUnseen) return false;
            if (node.kind == VNNodeKind.Menu || node.kind == VNNodeKind.MiniGame) return false;
            return IsSeen(node.id);
        }

        /// <summary>Spacer and structural nodes never stop the reader; empty lines are dropped too.</summary>
        private bool IsAutoSkippable(VNNode node)
        {
            switch (node.kind)
            {
                case VNNodeKind.Blank:
                case VNNodeKind.Unknown:
                case VNNodeKind.SceneTitle:
                case VNNodeKind.BranchTitle:
                case VNNodeKind.GlossaryEntry:
                case VNNodeKind.Conditional:
                    return true;
                case VNNodeKind.Menu:
                case VNNodeKind.MiniGame:
                    return false;
                default:
                    return string.IsNullOrEmpty(LocalizedText(node));
            }
        }

        // ------------------------------------------------------------------ text and speakers

        private string LocalizedText(VNNode node)
        {
            var settings = SettingsNow();
            if (settings.devShowKeys && !string.IsNullOrEmpty(node.id)) return node.id;

            var localizer = deps.Localizer;
            if (localizer != null && !string.IsNullOrEmpty(node.id))
            {
                if (localizer.TryGet(node.id, out var byId) && !string.IsNullOrEmpty(byId)) return byId;
            }

            if (node.kind == VNNodeKind.Menu) return node.menuPrompt ?? node.text ?? string.Empty;
            return node.text ?? string.Empty;
        }

        private string ResolveSpeaker(VNNode node)
        {
            if (node.kind != VNNodeKind.Dialogue && node.kind != VNNodeKind.InnerVoice) return null;

            var localizer = deps.Localizer;
            if (!string.IsNullOrEmpty(node.speaker) && localizer != null)
            {
                var name = localizer.SpeakerDisplayName(node.speaker);
                if (!string.IsNullOrEmpty(name)) return name;
            }
            if (!string.IsNullOrEmpty(node.speaker)) return node.speaker;

            switch (node.role)
            {
                case VNSpeakerRole.Player: return KeyText(VNDefaults.KeySpeakerPlayer);
                case VNSpeakerRole.InnerVoice: return KeyText(VNDefaults.KeySpeakerInnerVoice);
                case VNSpeakerRole.System: return KeyText(VNDefaults.KeySpeakerSystem);
                default: return null;
            }
        }

        private string OptionText(VNNode menu, int index, VNMenuOption option)
        {
            if (option == null) return string.Empty;
            var localizer = deps.Localizer;
            if (localizer != null)
            {
                if (!string.IsNullOrEmpty(option.id) && localizer.TryGet(option.id, out var byId) && !string.IsNullOrEmpty(byId))
                    return byId;
                if (!string.IsNullOrEmpty(option.label) && localizer.TryGet(option.label, out var byLabel) && !string.IsNullOrEmpty(byLabel))
                    return byLabel;
            }
            if (!string.IsNullOrEmpty(option.label)) return option.label;
            return KeyText(VNDefaults.KeyChoicesHint) + " " + (index + 1).ToString(CultureInfo.InvariantCulture);
        }

        private string KeyText(string key)
        {
            var localizer = deps.Localizer;
            if (localizer == null) return string.Empty;
            return localizer.Get(key) ?? string.Empty;
        }

        private VNBacklogEntry RebuildEntry(string nodeId)
        {
            var registry = deps.Registry;
            var node = registry != null ? registry.GetNode(nodeId) : null;
            if (node == null) return null;
            return new VNBacklogEntry
            {
                nodeId = nodeId,
                speakerName = ResolveSpeaker(node),
                text = LocalizedText(node),
                kind = node.kind
            };
        }

        // ------------------------------------------------------------------ input commands

        private void OnInputCommand(VNCommand command)
        {
            switch (command)
            {
                case VNCommand.Advance:
                    Advance();
                    break;
                case VNCommand.ToggleAuto:
                    SetMode(mode == VNRunMode.Auto ? VNRunMode.Manual : VNRunMode.Auto);
                    break;
                case VNCommand.ToggleSkip:
                    SetMode(mode == VNRunMode.SkipUnseen ? VNRunMode.Manual : VNRunMode.SkipUnseen);
                    break;
                case VNCommand.ToggleFast:
                    SetMode(mode == VNRunMode.Fast ? VNRunMode.Manual : VNRunMode.Fast);
                    break;
                case VNCommand.QuickSave:
                    QuickSave();
                    break;
                case VNCommand.QuickLoad:
                    QuickLoad();
                    break;
                default:
                    // OpenMenu/CloseMenu/Backlog/Glossary/Settings/HideUi/Navigate belong to the UI screens.
                    break;
            }
        }

        private void OnInputChoice(int index)
        {
            if (choicePending) Choose(index);
        }

        /// <summary>Writes the quickslot; warns once when the save layer is not wired.</summary>
        public void QuickSave()
        {
            var save = deps.Save;
            if (save == null)
            {
                WarnOnce("no-save", "QuickSave without ISaveService");
                return;
            }
            var slot = save.QuicksaveSlotId;
            if (string.IsNullOrEmpty(slot))
            {
                WarnOnce("no-quicksave-slot", "ISaveService.QuicksaveSlotId is empty");
                return;
            }
            if (save.Save(slot, CaptureState())) VNLog.Info(logArea, "quicksaved to " + slot);
        }

        /// <summary>Reads the quickslot; a broken one only warns, the current state stays on screen.</summary>
        public void QuickLoad()
        {
            var save = deps.Save;
            if (save == null)
            {
                WarnOnce("no-save", "QuickLoad without ISaveService");
                return;
            }
            var slot = save.QuicksaveSlotId;
            if (string.IsNullOrEmpty(slot))
            {
                WarnOnce("no-quicksave-slot", "ISaveService.QuicksaveSlotId is empty");
                return;
            }
            if (!save.TryLoad(slot, out var state))
            {
                VNLog.Warn(logArea, "quickload failed from " + slot);
                return;
            }
            RestoreState(state);
        }

        // ------------------------------------------------------------------ autosave

        private void Autosave(bool force)
        {
            var save = deps.Save;
            if (save == null) return;
            var slot = save.AutosaveSlotId;
            if (string.IsNullOrEmpty(slot))
            {
                WarnOnce("no-autosave-slot", "ISaveService.AutosaveSlotId is empty");
                return;
            }
            var key = (currentNode != null ? currentNode.id : currentBranch != null ? currentBranch.id : "start") + "|" + nodeIndexInBranch;
            if (!force && string.Equals(key, lastAutosaveKey, StringComparison.Ordinal)) return;
            lastAutosaveKey = key;
            if (save.Save(slot, CaptureState())) VNLog.Info(logArea, "autosaved at " + key);
        }

        // ------------------------------------------------------------------ settings and language

        private VNSettings SettingsNow()
        {
            var store = deps.Settings;
            var settings = store != null ? store.Current : null;
            return settings ?? DefaultSettings;
        }

        private static readonly VNSettings DefaultSettings = new VNSettings();

        private VNLanguage LanguageNow()
        {
            var localizer = deps.Localizer;
            if (localizer != null) return localizer.Language;
            return SettingsNow().language;
        }

        private void OnSettingsChanged(VNSettings settings)
        {
            if (settings == null) return;
            history.Resize(Clamp(settings.backlogMaxEntries, VNDefaults.BacklogMin, VNDefaults.BacklogMax));
            ApplyVolumes(settings);
            if (deps.Localizer != null && deps.Localizer.Language != settings.language) deps.Localizer.SetLanguage(settings.language);
        }

        private void ApplyVolumes(VNSettings s)
        {
            var audio = deps.Audio;
            if (audio == null) return;
            audio.SetBusVolume(VNAudioBus.Master, Clamp(s.masterVolume, 0f, 1f));
            audio.SetBusVolume(VNAudioBus.Music, Clamp(s.musicVolume, 0f, 1f));
            audio.SetBusVolume(VNAudioBus.Ambience, Clamp(s.ambienceVolume, 0f, 1f));
            audio.SetBusVolume(VNAudioBus.Sfx, Clamp(s.sfxVolume, 0f, 1f));
            audio.SetBusVolume(VNAudioBus.Voice, Clamp(s.voiceVolume, 0f, 1f));
        }

        /// <summary>Re-reads the on-screen line when the tables switch language (definition of done 5).</summary>
        private void OnLanguageChanged(VNLanguage language)
        {
            if (currentNode == null || currentBranch == null) return;
            var text = LocalizedText(currentNode);
            currentText = text;
            EmitNode(currentNode, currentCues, text);
        }

        // ------------------------------------------------------------------ helpers

        private VNBranchDef ResolveBranch(VNDataRegistry registry, VNGameState state)
        {
            if (!string.IsNullOrEmpty(state.branchId))
            {
                var byId = registry.GetBranch(state.branchId);
                if (byId != null) return byId;
            }
            if (!string.IsNullOrEmpty(state.nodeId))
            {
                var node = registry.GetNode(state.nodeId);
                var parsed = new VNNodeId(state.nodeId);
                var byNode = registry.GetBranch(parsed.BranchId);
                if (byNode != null && IndexOf(byNode, state.nodeId) >= 0) return byNode;
                if (node != null) return FindBranchOf(registry, node, state.nodeId);
            }
            return null;
        }

        private static VNBranchDef FindBranchOf(VNDataRegistry registry, VNNode node, string nodeId)
        {
            var parsed = new VNNodeId(nodeId);
            var direct = registry.GetBranch(parsed.BranchId);
            if (direct != null && IndexOf(direct, nodeId) >= 0) return direct;

            var scenes = registry.Scenes;
            for (var s = 0; s < scenes.Count; s++)
            {
                var branches = scenes[s].branches;
                if (branches == null) continue;
                for (var b = 0; b < branches.Count; b++)
                {
                    var branch = branches[b];
                    if (branch == null || branch.nodes == null) continue;
                    for (var i = 0; i < branch.nodes.Count; i++)
                    {
                        var n = branch.nodes[i];
                        if (n == node) return branch;
                        if (n != null && string.Equals(n.id, nodeId, StringComparison.Ordinal)) return branch;
                    }
                }
            }
            return null;
        }

        private static int IndexOf(VNBranchDef branch, string nodeId)
        {
            if (branch == null || branch.nodes == null || string.IsNullOrEmpty(nodeId)) return -1;
            for (var i = 0; i < branch.nodes.Count; i++)
            {
                var node = branch.nodes[i];
                if (node != null && string.Equals(node.id, nodeId, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        private void MarkSeen(VNNode node)
        {
            if (node != null) MarkSeenRaw(node.id);
        }

        private void MarkSeenRaw(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || seen.Contains(nodeId)) return;
            seen.Add(nodeId);
            seenOrder.Add(nodeId);
            flags.Set(VNDefaults.SeenFlagPrefix + nodeId, true);
        }

        // IConditionSource --------------------------------------------------

        bool IConditionSource.IsSeen(string nodeId) => IsSeen(nodeId);

        bool IConditionSource.TryFlag(string name, out VNConditionValue value)
        {
            if (flags.TryGet(name, out var stored))
            {
                value = ToCondition(stored);
                return true;
            }
            value = default;
            return false;
        }

        int IConditionSource.ChosenIndex(string menuId)
        {
            if (string.IsNullOrEmpty(menuId)) return -1;
            if (!flags.TryGet(VNDefaults.ChosenFlagPrefix + menuId, out var v)) return -1;
            if (v.kind == VNFlagKind.Bool) return v.boolValue ? 0 : -1;
            var n = v.AsNumber();
            if (double.IsNaN(n) || n < 0) return -1;
            return (int)n;
        }

        bool IConditionSource.WasChosen(string menuId) => ((IConditionSource)this).ChosenIndex(menuId) >= 0;

        private static VNConditionValue ToCondition(VNFlagValue v)
        {
            switch (v.kind)
            {
                case VNFlagKind.Bool: return VNConditionValue.From(v.boolValue);
                case VNFlagKind.Int: return VNConditionValue.From((double)v.intValue);
                case VNFlagKind.Float: return VNConditionValue.From(v.floatValue);
                case VNFlagKind.Str: return VNConditionValue.FromText(v.AsText());
                default: return VNConditionValue.Nothing();
            }
        }

        private void WarnOnce(string key, string message)
        {
            if (warnedKeys.Count > 64 || warnedKeys.Contains(key)) return;
            warnedKeys.Add(key);
            VNLog.Warn(logArea, message);
        }

        private static float Clamp(float value, float lo, float hi)
        {
            if (value < lo) return lo;
            return value > hi ? hi : value;
        }

        private static int Clamp(int value, int lo, int hi)
        {
            if (value < lo) return lo;
            return value > hi ? hi : value;
        }

        /// <summary>Stops timers and unsubscribes from every service this runtime listened to.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CancelPendingTimers();
            if (deps.Settings != null) deps.Settings.Changed -= OnSettingsChanged;
            if (deps.Localizer != null) deps.Localizer.LanguageChanged -= OnLanguageChanged;
            if (deps.Input != null)
            {
                deps.Input.Command -= OnInputCommand;
                deps.Input.ChoiceSelected -= OnInputChoice;
                deps.Input.Disable();
            }
        }
    }
}
