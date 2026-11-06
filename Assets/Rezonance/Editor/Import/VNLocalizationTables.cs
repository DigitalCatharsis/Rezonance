// REZONANCE VN EDITOR - localization table writer (ru.csv / en.csv / ui.csv).
// RFC4180 escaping, UTF-8 with BOM so Excel opens the Cyrillic columns correctly.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Rezonance.VN.Core;

namespace Rezonance.VN.Import
{
    /// <summary>One localization row with both language columns.</summary>
    public sealed class VNL10nEntry
    {
        public string Key;
        public string Ru;
        public string En;
        public string Note;
        /// <summary>"node" for script text, "ui" / "name" / "gl" for interface rows.</summary>
        public string Group;

        public VNL10nEntry(string key, string ru, string en, string note, string group)
        {
            Key = key;
            Ru = ru ?? string.Empty;
            En = en ?? string.Empty;
            Note = note ?? string.Empty;
            Group = group;
        }
    }

    /// <summary>
    /// Generates the CSV tables of ARCHITECTURE.md sections 7 and 9.8. ru.csv and en.csv carry the same key
    /// set (EN script text is empty until the translation pass), ui.csv is the bilingual reference of the
    /// canonical interface keys, name.* and gl.* rows.
    /// </summary>
    public static class VNLocalizationTables
    {
        const string Header = "key,text,note";

        /// <summary>Canonical interface keys with the RU seed value and the EN translation.</summary>
        static readonly string[][] UiRows =
        {
            new[] { "ui.main.title", "РЕЗОНАНС", "REZONANCE" },
            new[] { "ui.main.new_game", "Новая игра", "New game" },
            new[] { "ui.main.continue", "Продолжить", "Continue" },
            new[] { "ui.main.load", "Загрузить", "Load" },
            new[] { "ui.main.settings", "Настройки", "Settings" },
            new[] { "ui.main.language", "Язык", "Language" },
            new[] { "ui.main.quit", "Выход", "Quit" },
            new[] { "ui.main.version", "Версия", "Version" },

            new[] { "ui.game.auto", "Авто", "Auto" },
            new[] { "ui.game.skip", "Пропуск", "Skip" },
            new[] { "ui.game.fast", "Быстро", "Fast" },
            new[] { "ui.game.log", "Журнал", "Log" },
            new[] { "ui.game.save", "Сохранить", "Save" },
            new[] { "ui.game.load", "Загрузить", "Load" },
            new[] { "ui.game.menu", "Меню", "Menu" },
            new[] { "ui.game.hide_ui", "Скрыть интерфейс", "Hide UI" },
            new[] { "ui.game.choices_hint", "Выберите пункт", "Make a choice" },
            new[] { "ui.game.minigame_placeholder", "Мини-игра ещё не собрана", "Mini-game is not built yet" },
            new[] { "ui.game.minigame_continue", "Продолжить", "Continue" },

            new[] { "ui.menu.title", "Пауза", "Paused" },
            new[] { "ui.menu.resume", "Продолжить", "Resume" },
            new[] { "ui.menu.save", "Сохранить", "Save" },
            new[] { "ui.menu.load", "Загрузить", "Load" },
            new[] { "ui.menu.settings", "Настройки", "Settings" },
            new[] { "ui.menu.glossary", "Глоссарий", "Glossary" },
            new[] { "ui.menu.backlog", "Журнал", "Backlog" },
            new[] { "ui.menu.quit_to_menu", "В главное меню", "Back to main menu" },
            new[] { "ui.menu.quit_game", "Выйти из игры", "Quit game" },

            new[] { "ui.save.title", "Сохранения", "Saves" },
            new[] { "ui.save.slot", "Слот {0}", "Slot {0}" },
            new[] { "ui.save.autosave", "Автосохранение", "Autosave" },
            new[] { "ui.save.quicksave", "Быстрое сохранение", "Quicksave" },
            new[] { "ui.save.empty", "Пусто", "Empty" },
            new[] { "ui.save.delete", "Удалить", "Delete" },
            new[] { "ui.save.confirm_delete", "Удалить это сохранение?", "Delete this save?" },
            new[] { "ui.save.corrupted", "Файл повреждён", "File corrupted" },
            new[] { "ui.save.newer_version", "Сохранение из новой версии", "Save from a newer version" },
            new[] { "ui.save.saved_at", "Сохранено: {0}", "Saved: {0}" },
            new[] { "ui.save.playtime", "Время в игре: {0}", "Playtime: {0}" },
            new[] { "ui.save.unknown_scene", "Неизвестная сцена", "Unknown scene" },

            new[] { "ui.settings.title", "Настройки", "Settings" },
            new[] { "ui.settings.master", "Общая громкость", "Master volume" },
            new[] { "ui.settings.music", "Музыка", "Music" },
            new[] { "ui.settings.ambience", "Атмосфера", "Ambience" },
            new[] { "ui.settings.sfx", "Эффекты", "Effects" },
            new[] { "ui.settings.voice", "Голос", "Voice" },
            new[] { "ui.settings.text_speed", "Скорость текста", "Text speed" },
            new[] { "ui.settings.auto_delay", "Пауза авто", "Auto delay" },
            new[] { "ui.settings.read_delay", "Пауза на чтение", "Reading delay" },
            new[] { "ui.settings.language", "Язык", "Language" },
            new[] { "ui.settings.fullscreen", "Полный экран", "Fullscreen" },
            new[] { "ui.settings.show_keys", "Показывать клавиши", "Show key hints" },
            new[] { "ui.settings.reset", "Сбросить", "Reset" },
            new[] { "ui.settings.close", "Закрыть", "Close" },

            new[] { "ui.backlog.title", "Журнал", "Backlog" },
            new[] { "ui.backlog.empty", "Здесь пока нечего показать", "Nothing to show yet" },

            new[] { "ui.glossary.title", "Глоссарий", "Glossary" },
            new[] { "ui.glossary.search", "Поиск", "Search" },
            new[] { "ui.glossary.empty", "Ничего не найдено", "Nothing found" },

            new[] { "ui.common.ok", "ОК", "OK" },
            new[] { "ui.common.cancel", "Отмена", "Cancel" },
            new[] { "ui.common.close", "Закрыть", "Close" },
            new[] { "ui.common.yes", "Да", "Yes" },
            new[] { "ui.common.no", "Нет", "No" },
            new[] { "ui.common.on", "Вкл", "On" },
            new[] { "ui.common.off", "Выкл", "Off" },

            new[] { "ui.toast.saved", "Сохранено", "Saved" },
            new[] { "ui.toast.loaded", "Загружено", "Loaded" },
            new[] { "ui.toast.load_failed", "Не удалось загрузить", "Load failed" },
            new[] { "ui.toast.settings_saved", "Настройки сохранены", "Settings saved" },

            new[] { "ui.speaker.player", "Вы", "You" },
            new[] { "ui.speaker.inner_voice", "Внутренний голос", "Inner voice" },
            new[] { "ui.speaker.system", "Система", "System" }
        };

        /// <summary>
        /// EN name plates keyed by the VNSlug of the source label (so "liberti" is the Cyrillic spelling of
        /// ЛИБЕРТИ and the display name stays "Liberty"). Transliteration of the slug is the fallback.
        /// </summary>
        static readonly Dictionary<string, string> SpeakerEnglishBySlug = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "irena", "Irena" },
            { "gloria", "Gloria" },
            { "agata", "Agata" },
            { "liberti", "Liberty" },
            { "teren", "Teren" },
            { "minerva", "Minerva" },
            { "nika", "Nika" },
            { "ruta", "Ruta" },
            { "barmen", "Bartender" },
            { "salvator", "Salvator" },
            { "liora", "Liora" },
            { "semen", "Semyon" },
            { "vy", "You" },
            { "golos-za-steklom", "Voice behind glass" },
            { "vtoroy-golos", "Second voice" },
            { "vtoroy", "Second" },
            { "golos-za-dveryu", "Voice behind the door" },
            { "golos-iz-pamyati", "Voice from memory" },
            { "golos-iz-terminala", "Voice from the terminal" },
            { "golos-sistemy", "System voice" },
            { "golos-sluzhby", "Service voice" },
            { "golos-sotrudnika", "Staff voice" },
            { "golos-vedushchey", "Host voice" },
            { "zhenskiy-golos", "Woman's voice" },
            { "zhenskiy-golos-iz-dinamika", "Woman's voice over the speaker" },
            { "zhenshchina", "Woman" },
            { "devushka", "Girl" },
            { "neizvestnaya", "Unknown woman" },
            { "muzhchina", "Man" },
            { "malchik", "Boy" },
            { "rebonok", "Child" },
            { "babushka", "Grandmother" },
            { "okhrannik", "Guard" },
            { "sotrudnik", "Staff member" },
            { "sluzhashchiy", "Clerk" },
            { "tekhnik", "Technician" },
            { "master", "Repairman" },
            { "patsient", "Patient" },
            { "podrostok", "Teenager" },
            { "prodavets", "Vendor" },
            { "prodavshchitsa", "Saleswoman" },
            { "torgovets", "Trader" },
            { "kurer", "Courier" },
            { "klient", "Client" },
            { "gruzchik", "Loader" },
            { "starshiy-gruzchik", "Senior loader" },
            { "napadayushchiy", "Attacker" },
            { "chelovek", "Person" },
            { "chelovek-v-kurtke", "Man in the jacket" },
            { "chelovek-u-schityvatelaya", "Man at the scanner" },
            { "ekran", "Screen" },
            { "ekran-peredatchika", "Transmitter screen" },
            { "terminal", "Terminal" },
            { "arhivnaya-stroka", "Archive line" }
        };

        /// <summary>Moves if a Cyrillic literal ever gets mangled by an encoding accident; printed in the report.</summary>
        public static int RuTableChecksum
        {
            get
            {
                var sum = 0;
                for (var i = 0; i < UiRows.Length; i++)
                {
                    var ru = UiRows[i][1];
                    for (var c = 0; c < ru.Length; c++) sum += ru[c];
                    var en = UiRows[i][2];
                    for (var c = 0; c < en.Length; c++) sum -= en[c];
                }
                return sum;
            }
        }

        /// <summary>Number of canonical ui.* keys shipped in the table.</summary>
        public static int CanonicalUiKeyCount { get { return UiRows.Length; } }

        // ---------------------------------------------------------------- build

        /// <summary>Builds every localization row of the act in a stable, document-order sequence.</summary>
        public static List<VNL10nEntry> BuildEntries(VNScriptData data, IReadOnlyList<VNSpeakerRow> speakerRows)
        {
            var entries = new List<VNL10nEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var act = data == null || string.IsNullOrEmpty(data.actId) ? "act1" : data.actId;

            if (data != null && data.scenes != null)
            {
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
                            if (node == null || string.IsNullOrEmpty(node.id)) continue;

                            var nodeText = node.kind == VNNodeKind.Menu ? node.menuPrompt : node.text;
                            AddUnique(entries, seen, new VNL10nEntry(node.id, nodeText, string.Empty,
                                Note(act, node.sourceLine, node.speaker), "node"));

                            if (node.options == null) continue;
                            for (var o = 0; o < node.options.Count; o++)
                            {
                                var option = node.options[o];
                                if (option == null || string.IsNullOrEmpty(option.id)) continue;
                                AddUnique(entries, seen, new VNL10nEntry(option.id, option.label, string.Empty,
                                    Note(act, node.sourceLine, null) + "|menu:" + node.id + "|opt:" + (o + 1), "node"));
                            }
                        }
                    }
                }
            }

            for (var i = 0; i < UiRows.Length; i++)
                AddUnique(entries, seen, new VNL10nEntry(UiRows[i][0], UiRows[i][1], UiRows[i][2], "canonical:9.8", "ui"));

            var names = new List<VNSpeakerRow>();
            if (speakerRows != null)
                for (var i = 0; i < speakerRows.Count; i++)
                    if (speakerRows[i] != null && !string.IsNullOrEmpty(speakerRows[i].label)) names.Add(speakerRows[i]);
            names.Sort(delegate(VNSpeakerRow a, VNSpeakerRow b) { return string.CompareOrdinal(a.label, b.label); });
            for (var i = 0; i < names.Count; i++)
            {
                var label = names[i].label;
                var key = string.IsNullOrEmpty(names[i].nameKey) ? VNImportNaming.NameKey(label) : names[i].nameKey;
                AddUnique(entries, seen, new VNL10nEntry(key, label, EnglishNameOf(label),
                    "speaker:" + names[i].role + "|slot:" + names[i].defaultSlot, "name"));
            }

            if (data != null && data.glossary != null)
            {
                for (var i = 0; i < data.glossary.Count; i++)
                {
                    var g = data.glossary[i];
                    if (g == null || string.IsNullOrEmpty(g.id)) continue;
                    // TODO(contract): EN glossary text is a translation pass; the key is reserved so the UI can
                    // switch language without a schema change.
                    AddUnique(entries, seen, new VNL10nEntry(g.id, g.definition, string.Empty,
                        "glossary:" + g.term + "|en:pending", "gl"));
                    AddUnique(entries, seen, new VNL10nEntry(g.id + ".term", g.term, g.term, "glossary-term", "gl"));
                }
            }

            return entries;
        }

        static void AddUnique(List<VNL10nEntry> entries, HashSet<string> seen, VNL10nEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Key)) return;
            if (!seen.Add(entry.Key)) return;
            entries.Add(entry);
        }

        static string Note(string act, int line, string speaker)
        {
            var note = "src:" + act + "|line:" + line;
            return string.IsNullOrEmpty(speaker) ? note : note + "|speaker:" + speaker;
        }

        // ---------------------------------------------------------------- write

        /// <summary>Writes the three tables. Returns the number of keys.</summary>
        public static int WriteTables(VNScriptData data, IReadOnlyList<VNSpeakerRow> speakerRows,
                                     string ruPath, string enPath, string uiPath)
        {
            var entries = BuildEntries(data, speakerRows);

            var ruRows = new List<string[]>();
            var enRows = new List<string[]>();
            var uiRows = new List<string[]>();
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                ruRows.Add(new[] { e.Key, e.Ru, e.Note });
                enRows.Add(new[] { e.Key, e.En, e.Note });
                if (e.Group != "node") uiRows.Add(new[] { e.Key, e.Ru, e.Note + "|en=" + e.En });
            }

            WriteCsv(ruPath, ruRows);
            WriteCsv(enPath, enRows);
            WriteCsv(uiPath, uiRows);
            return entries.Count;
        }

        /// <summary>EN name for a label with no explicit mapping: transliterated upper case.</summary>
        public static string EnglishNameOf(string label)
        {
            if (string.IsNullOrEmpty(label)) return string.Empty;
            var slug = VNSlug.Of(label);
            string en;
            return SpeakerEnglishBySlug.TryGetValue(slug, out en) ? en : TransliterateToUpper(label);
        }

        static string TransliterateToUpper(string label)
        {
            return VNSlug.Of(label ?? string.Empty).Replace('-', ' ').ToUpperInvariant();
        }

        // ---------------------------------------------------------------- csv core

        /// <summary>RFC4180: quote when the value holds a comma, a quote or a line break; double inner quotes.</summary>
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var needsQuotes = value.IndexOf(',') >= 0 || value.IndexOf('"') >= 0 ||
                              value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0;
            if (!needsQuotes) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>Writes the table as UTF-8 with BOM and CRLF line ends.</summary>
        public static void WriteCsv(string assetPath, List<string[]> rows)
        {
            var full = VNImportSettings.ToAbsolute(assetPath);
            var folder = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);

            var sb = new StringBuilder();
            sb.Append(Header);
            for (var i = 0; i < rows.Count; i++)
            {
                sb.Append("\r\n");
                sb.Append(Escape(rows[i][0])).Append(',');
                sb.Append(Escape(Row(rows[i], 1))).Append(',');
                sb.Append(Escape(Row(rows[i], 2)));
            }
            File.WriteAllText(full, sb.ToString(), new UTF8Encoding(true));
        }

        static string Row(string[] row, int index)
        {
            return row != null && row.Length > index ? row[index] : string.Empty;
        }
    }
}
