# РЕЗОНАНС — VN-движок: архитектура и правила сборки

Документ-контракт. Все модули пишутся строго под него. Если модулю нужно то, чего здесь нет, —
он не изобретает публичный API молча, а оставляет `// TODO(contract): …` и сообщает в отчёте.

## 0. Решения, которые уже приняты (не оспариваются)

- **Источник истины по тексту** — `Resonance_ACT_1_Readable_V9.md` (Editor-импортёр). В рантайме
  живут только сгенерированные ассеты. Правки текста = переимпорт markdown, никогда — ручная правка
  ассетов.
- **Слой постановки** (фон, портрет, звук, тайминги) — отдельный редактируемый оверлей, ключуемый
  ID узлов. Импортёр его дополняет, но не перетирает.
- **UI**: uGUI + TextMeshPro. Никакого UI Toolkit.
- **Языки**: RU (исходник) + EN. Все видимые строки идут через ключ, хардкод строк в коде запрещён
  (кроме дебага).
- **Платформы**: PC (Windows) + Android. Базовое разрешение 1920×1080, 16:9, `CanvasScaler`
  `matchWidthOrHeight = 0.5`, учёт safe area на телефоне.
- **Unity**: 6000.6.0f1, URP 17.6, Input System (только новый, `activeInputHandler: 1`), UniTask
  доступен, TMP входит в `com.unity.ugui`.
- **Сборки без asmdef**: весь код идёт в `Assembly-CSharp` / `Assembly-CSharp-Editor` (Editor-скрипты
  обязательно под папку `Editor/`). Assembly definitions добавим после первой зелёной компиляции.

## 1. Разметка источника (что реально есть в V9)

| Конструкция | В markdown | В движке |
|---|---|---|
| Сцена | `## Белая комната` | `VNBranch`? нет — `VNScene` |
| Ветка | `### «Что вы мне ввели?»` | `VNBranch` |
| Разделитель | `---` | игнорируется (структурный шум) |
| Реплика NPC | `ИРЕНА: «Текст.»` | `VNDialogue` (speaker = `ИРЕНА`) |
| Бриф говорящего | `ИРЕНА (шёпотом): «…»` | `VNDialogue.delivery = "шёпотом"` |
| Реплика игрока | `Вы: «…»` | `VNDialogue` (speaker = `Вы`) |
| Внутренний голос | `Вы (внутренний голос): «…»` | `VNInnerVoice` |
| Ремарка/действие | `[Он проверяет замок.]` | `VNRemark` |
| Экранный текст | `` `ОТКЛОНЕНИЙ НЕТ` `` внутри ремарки или строкой | `VNScreenText` |
| Меню | `**Что делаете Вы:**` + `- «…»` | `VNMenu(prompt, options)` |
| Пункт меню | `- Попытаться произнести имя.` | `VNMenuOption(targetBranchId)` |
| Условная вставка | `*(Если Вы спросите ещё раз:)*` | `VNConditional` (condition из оверлея) |
| Мини-игра | `[Мини-игра]` | `VNMiniGame` |
| Глоссарий | `**Термин** — определение` в хвосте | `VNGlossaryEntry` |

Счётчики V9 (ориентир для валидации): 63 сцены (`## `), 457 веток (`### `), 111 меню-блоков,
77 условных вставок, 12 `[Мини-игра]`, 54 строки с бэктиками.

**Правило возврата из-под меню.** Опции меню ведут на ветки, перечисленные сразу после меню;
магистраль продолжается веткой, идущей **после последней опционной** (в «Белой комнате» это
`### Общий финал процедуры`). Импортёр вычисляет её и пишет в `VNNode.resumeBranchId` узла меню.
Если после опционных веток другой ветки нет — resume = первая ветка следующей сцены. Это правило
обязано быть в валидационном отчёте по каждому меню, где оно не сработало.

## 2. ID-схема (стабильность важнее красоты)

`{act}.{scene-slug}.{branch-slug}.{kind}{ordinal}` — всё в lower-ASCII, транслитерация кириллицы,
пробелы → `-`, не-буквы выкидываются, при коллизии добавляется `-2`, `-3`.

Пример: `act1.belaya-komnata.chto-vy-mne-vveli.d003`.

Правила:
- ID узла не меняется при правке соседнего текста (поэтому ordinal считается внутри ветки, а не
  глобально, и слаг берётся из заголовка, а не из позиции).
- ID ветки = `act1.{scene}.{branch}`; ID сцены = `act1.{scene}`.
- Ключ локализации узла == ID узла. Ключ UI == `ui.{screen}.{element}`.
- Транслитерация — общий маппер `VNSlug` (см. `Runtime/Core/VNIds.cs`), импортёр и тулзы используют
  только его.

## 3. Папки и владение файлами (конфликтов не быть должно)

```
Assets/Rezonance/
  ARCHITECTURE.md                  ← этот файл
  Runtime/
    Core/         VNEnums.cs, VNData.cs, VNContracts.cs, VNIds.cs   (владелец: ядрo/редактор контрактов)
    Engine/       VNRuntime.cs, ConditionEvaluator.cs, FlagLedger.cs,
                  Backlog.cs, Typewriter.cs, VNInput.cs              (агент «рантайм»)
    Presentation/ BackdropDirector.cs, PortraitDirector.cs,
                  AudioDirector.cs, ScreenTextPresenter.cs,
                  MiniGameStub.cs, AssetLibrary.cs                  (агент «презентация»)
    UI/           UIBuilder.cs, VNGameView.cs, MainMenuScreen.cs,
                  DialogueScreen.cs, ChoiceScreen.cs, PauseMenu.cs,
                  SaveLoadScreen.cs, SettingsScreen.cs,
                  BacklogScreen.cs, GlossaryScreen.cs, SafeArea.cs  (агент «UI»)
    Services/     SaveService.cs, LocalizationService.cs,
                  SettingsStore.cs, GameBootstrap.cs                (агент «UI», тот же, что и UI)
    Data/         (сгенерированные ассеты импортёра — руками не трогать)
  Editor/
    Import/       VNMarkdownImporter.cs, VNParser.cs, VNLocalizationTables.cs,
                  VNSpeakerMapGenerator.cs, VNValidationReport.cs    (агент «импортёр»)
    Tools/        VNFontGenerator.cs, VNPlaceholderArt.cs            (агент «импортёр»)
  Art/            backgrounds/, portraits/, ui/, fonts/
  Audio/          music/, ambience/, sfx/, voice/
  Localization/   ru.csv, en.csv, ui.csv
  Tests/          (потом, когда будет зелёная компиляция)
```

Никто не правит чужую папку. Общие типы только в `Runtime/Core/`.

## 4. Публичные контракты (код в `Runtime/Core/` — он уже написан и зелёный, менять только с пометкой в отчёте)

- Модель данных: **плоский узел** `VNNode` (JsonUtility не умеет полиморфизм, а узлов десятки тысяч) →
  `VNBranchDef` → `VNSceneDef` → `VNScriptData`. Реестр и O(1)-поиск: `VNDataRegistry.Build(data)`.
- Идентификаторы: `VNSlug` + `VNIdFactory` (`Runtime/Core/VNIds.cs`).
- Постановка: `VNStageOverlay` (ScriptableObject, слияние полей Scene → Branch → Node) и
  `VNSpeakerMap` (метка спикера → персонаж/роль/слот). Импортёр сеет их заглушками, люди правят.
- Движок: `IVNRuntime` — `StartAct/StartBranch/JumpToNode/Advance/Choose/SetMode/CompleteTyping/
  CaptureState/RestoreState`, события `NodeChanged`, `MenuOpened/MenuClosed`, `BranchChanged`,
  `SceneChanged`, `Finished`, `MiniGameRequested`.
- View: `IVNRuntimeView` (реализует UI-модуль), ввод: `IVNInput` + `VNCommand` (реализует engine-модуль,
  consumes — UI). Legacy `UnityEngine.Input` запрещён: в проекте только новый Input System.
- Презентация: `IBackdropDirector`, `IPortraitDirector`, `IAudioDirector`, `IScreenTextPresenter`,
  `IVNMiniGameHost` — движок их только дёргает, про Unity-объекты ничего не знает.
- Сервисы: `ILocalizer`, `ISaveService` (+ `VNGameState`, `SaveSlotInfo`), `ISettingsStore` (+ `VNSettings`).
- Composition root: `GameBootstrap` собирает всё (пишу я на интеграции, агенты его не трогают).

## 4.1 Куда кладутся сгенерированные данные (Runtime-loadable через Resources)

```
Assets/Rezonance/Resources/Rezonance/data/act1.json      ← VNScriptData
Assets/Rezonance/Resources/Rezonance/l10n/ru.csv         ← ключ = ID узла
Assets/Rezonance/Resources/Rezonance/l10n/en.csv
Assets/Rezonance/Resources/Rezonance/l10n/ui.csv         ← ключи ui.*
Assets/Rezonance/Resources/Rezonance/art/bg/*.png        ← bg_{location}_{variant}
Assets/Rezonance/Resources/Rezonance/art/por/*.png       ← por_{character}_{emotion}
Assets/Rezonance/Resources/Rezonance/audio/{music,ambience,sfx,voice}/
Assets/Rezonance/Data/                 ← VNStageOverlay.asset, VNSpeakerMap.asset (не Resources)
Assets/Rezonance/Reports/vn_import_report.md
```

## 5. Рантайм-поток одного узла

1. `VNRuntime` берёт следующий узел текущей ветки.
2. Спрашивает `IStageOverlay` про cues для этого ID (фон/портреты/музыка/тайминг).
3. Шлёт cue в directors, затем текст в `IVNRuntimeView` (UI).
4. Ждёт ввода (Advance / Choose / автотайминг).
5. Пишет в `Backlog`, обновляет `FlagLedger` (у men-опций — `chosen`, у узлов — `seen`).
6. На меню — пауза до `Choose(i)`, затем переход на ветку.
7. Автосейв по состоянию (конец ветки, меню, сцена-переход).

## 6. Сохранения

- `Application.persistentDataPath/Rezonance/saves/slot_{n}.json`.
- `JsonUtility` + `[Serializable]` DTO, словари — списками пар. Никаких внешних JSON-библиотек.
- `SaveEnvelope { int formatVersion; string slotId; long unixTicks; SaveThumbnail thumb; GameState state; }`
  `GameState { nodeId; branchId; sceneId; flags[]; seen[]; choices[]; language; settingsRef; playtimeSeconds; }`
- Слоты: 12 ручных + `autosave` + `quicksave`. Миграция: `formatVersion` выше известной → не чиним,
  говорим юзеру «сейв из новой версии».
- Android: те же пути, никаких абсолютных win-путей.

## 7. Локализация

- Таблицы CSV (`Localization/ru.csv`, `en.csv`, `ui.csv`): `key,text,note`. Экранирование кавычек — RFC4180.
- `ILocalizer.CurrentLanguage`, `Get(key)`, `TryGet(key, out)`, `MissingKeyCount`; при отсутствии
  перевода — fallback на ru, а не на сам ключ (ключ в UI показываем только в dev-режиме).
- Шрифт на язык: TMP Font Asset per language, `FontProfile { language, regular, bold }`.
  Прототип — Arial (есть кириллица), боевой вариант — OFL-шрифт, замена в одном месте.
- Длина строк: UI-элементы не обрезаются, у реплик `enableWordWrapping`, у кнопок `TextMeshProUGUI`
  с `overflowMode = Shrink`.

## 8. Ассеты и плейсхолдеры

- Backend загрузки — `AssetLibrary` (пока `Resources.Load` по ключу; интерфейс позволяет переехать на
  Addressables, не трогая directors).
- Конвенция имён: `bg_{location}_{variant}`, `por_{character}_{emotion}`, `ui_{element}`,
  `mus_{cue}`, `amb_{cue}`, `sfx_{cue}`.
- Плейсхолдеры генерируются тулзой: ровная плашка + подпись (TMP/Texture2D), портрет — заглушка с
  кириллической плашкой имени. Реальные арты придут позже и встанут по тому же имени.
-Speaker map: `ИРЕНА → irena`, `ГЛОРИЯ → gloria`, `АГАТА → agata`, `ЛИБЕРТИ → liberty`,
  `ТЕРЕН → teren`, `МИНЕРВА → minerva`, `НИКА → nika`, `РУТА → ruta`, `БАРМЕН → bartender`,
  амбиентные голоса (`ЖЕНЩИНА`, `ГОЛОС ЗА СТЕКЛОМ`, `ВТОРОЙ ГОЛОС`, …) → без портрета, только имя.

## 9. Definition of done для прототипа

1. Reimport V9 без ошибок, валидационный отчёт сходится со счётчиками раздела 1.
2. Play в `VN_Play`-сцене: «Белая комната» читается до первого меню, меню переключает ветки.
3. Клавиша/тап листает, `Ctrl` — авто, `Tab`/`Esc` — меню, пробел — advance.
4. Save → Load → состояние (узел, флаги, язык, настройки) восстанавливается.
5. Смена языка в настройках мгновенно переключает реплики и UI, без рестарта.
6. Фон меняется по cue, портреты появляются/гаснут, экранный текст `…` показывается плашкой.
7. Компиляция чистая: `read_console` без errors.

## 9.5 Условные вставки: как гейтится содержимое

`*(Если …:)*` — это `VNNode(kind=Conditional)`, а всё, что идёт после него до следующего пустого
абзаца/маркера, остаётся **обычными узлами в той же ветке**. Правило: импортёр копирует выражение
условия в поле `conditionExpr` каждого узла вставки, а сам маркер помечает `conditionExpr` тоже.
Рантайм на каждом узле: `conditionExpr` непустой → вычислить → false = пропустить узел. Никаких
синтетических веток, порядок `branches` не портится. Непустой `conditionExpr` у узла без маркера
означает «ручная привязка из оверлея» — тоже валидно.

## 9.6 Слои Canvas (создаёт UI-модуль, потребляет presentation)

Имена фиксированы, порядок снизу вверх:
`VNCanvas/BackdropLayer`, `/PortraitLayer`, `/ScreenTextLayer`, `/DialogueLayer`, `/ChoiceLayer`,
`/ModalLayer`, `/HudLayer`, `/DebugLayer`. Canvas: Screen Space Overlay, `CanvasScaler` 1920×1080,
`matchWidthOrHeight = 0.5`, `GraphicRaycaster` с `ignoreReversedGraphics`, EventSystem на
InputSystemUIInputModule. Directors получают нужный `RectTransform` в `Init(...)` и сами себе создают
элементы внутри слоя.

## 9.7 Привязки ввода (единые для движка и для экрана настроек)

| Команда | Клавиатура/мышь | Тач |
|---|---|---|
| Advance | Space, Enter, ЛКМ | тап по DialogueLayer |
| Choose | 1–9 | клик по пункту (UI зовёт `runtime.Choose` сам) |
| ToggleAuto | Ctrl | кнопка HUD |
| ToggleSkip | Shift | кнопка HUD |
| ToggleFast | F | — |
| OpenMenu / CloseMenu | Esc, Tab | кнопка HUD |
| QuickSave / QuickLoad | F5 / F9 | кнопка HUD |
| OpenBacklog / OpenGlossary | H / J | кнопки меню |
| OpenSettings | F1 | кнопка меню |
| HideUi | `.` (точка) | кнопка HUD |

Legacy `UnityEngine.Input` не использовать нигде.

## 9.8 Ключи локализации интерфейса (канон; импортёр пишет `ui.csv`, UI только читает)

`ui.main.{title,new_game,continue,load,settings,language,quit,version}` ·
`ui.game.{auto,skip,fast,log,save,load,menu,hide_ui,choices_hint,minigame_placeholder,minigame_continue}` ·
`ui.menu.{title,resume,save,load,settings,glossary,backlog,quit_to_menu,quit_game}` ·
`ui.save.{title,slot,autosave,quicksave,empty,delete,confirm_delete,corrupted,newer_version,saved_at,playtime,unknown_scene}` ·
`ui.settings.{title,master,music,ambience,sfx,voice,text_speed,auto_delay,read_delay,language,fullscreen,show_keys,reset,close}` ·
`ui.backlog.{title,empty}` · `ui.glossary.{title,search,empty}` ·
`ui.common.{ok,cancel,close,yes,no,on,off}` · `ui.toast.{saved,loaded,load_failed,settings_saved}` ·
`ui.speaker.{player,inner_voice,system}` · `name.{label-slug}` и `gl.{term-slug}` — генерирует импортёр.

## 10. Стиль кода
- Namespace `Rezonance.VN.*`, файлы в кодировке UTF-8 **с** BOM (Unity иначе ломает кириллицу в
  строках-литералах — в литералах её, впрочем, быть не должно).
- Никаких `Debug.Log` без тега: `VNLog.Info/Warning/Error(area, msg)` с флагом dev-режима.
- UniTask для асинхронщины, корутины — только внутри presentation-директоров, где нужен кадр-тейлинг.
- Публичный API — с XML-доком в одну строку. Мелкие классы — лучше больше файлов, чем один giant.
- Всё, что читает таблицы/файлы, обязано быть устойчиво к CRLF и BOM.
