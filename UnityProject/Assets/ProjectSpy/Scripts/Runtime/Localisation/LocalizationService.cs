using System;
using System.Collections.Generic;
using R3;

namespace ProjectSpy.Unity.Localisation
{
    /// <summary>Languages the game ships.</summary>
    public enum UiLanguage
    {
        /// <summary>Thai. The default, because the game is written in Thai first.</summary>
        Thai = 0,

        /// <summary>English.</summary>
        English = 1,
    }

    /// <summary>
    /// Resolves localization keys to text, with Thai as the default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A missing key renders as the key.</b> Never as an exception, never as an empty
    /// string and never as a crash on a background thread. A missing row in one locale is a
    /// content bug that must be visible in the UI and logged once — not a reason for the
    /// frame to die while a mission is running.
    /// </para>
    /// <para>
    /// <b>Thai first.</b> The default is not a fallback order chosen for convenience; the
    /// game is authored in Thai and English is the parallel translation. Getting this
    /// backwards ships the wrong game to the wrong audience.
    /// </para>
    /// <para>
    /// Rows come from the compiled <c>localization</c> table, so a new name key is validated
    /// by the same table validator as everything else rather than by remembering to add a
    /// string in two places.
    /// </para>
    /// </remarks>
    public sealed class LocalizationService : Services.IProjectSpyService
    {
        private readonly Dictionary<string, Dictionary<UiLanguage, string>> _rows = new();
        private readonly HashSet<string> _reportedMissing = new();

        /// <summary>The active language. Thai unless explicitly changed.</summary>
        public UiLanguage Current { get; private set; } = UiLanguage.Thai;

        /// <summary>Raised when the language changes. Carries the new language.</summary>
        public Subject<UiLanguage> LanguageChanged { get; } = new();

        /// <summary>How many keys have been requested and were not found.</summary>
        public int MissingKeyCount => _reportedMissing.Count;

        /// <summary>Adds or replaces one row.</summary>
        public void Add(string key, UiLanguage language, string text)
        {
            if (string.IsNullOrEmpty(key))
                return;

            if (!_rows.TryGetValue(key, out var byLanguage))
            {
                byLanguage = new Dictionary<UiLanguage, string>();
                _rows[key] = byLanguage;
            }

            byLanguage[language] = text;
        }

        /// <summary>Loads every row for one language from a key/text pair list.</summary>
        public void AddRange(UiLanguage language, IEnumerable<KeyValuePair<string, string>> pairs)
        {
            foreach (var pair in pairs)
                Add(pair.Key, language, pair.Value);
        }

        /// <summary>Switches language.</summary>
        public void SetLanguage(UiLanguage language)
        {
            if (Current == language)
                return;

            Current = language;
            LanguageChanged.OnNext(language);
        }

        /// <summary>Toggles between the two shipped languages.</summary>
        public void ToggleLanguage()
            => SetLanguage(Current == UiLanguage.Thai ? UiLanguage.English : UiLanguage.Thai);

        /// <summary>
        /// Resolves a key in the active language.
        /// </summary>
        /// <remarks>
        /// Falls back to the other language when the active one has no row, which turns a
        /// half-finished translation into untranslated-but-readable text rather than a screen
        /// full of key names. Returns the key itself when neither has it, and logs once.
        /// </remarks>
        public string Get(string key)
        {
            if (string.IsNullOrEmpty(key))
                return string.Empty;

            if (_rows.TryGetValue(key, out var byLanguage))
            {
                if (byLanguage.TryGetValue(Current, out string text) && !string.IsNullOrEmpty(text))
                    return text;

                var other = Current == UiLanguage.Thai ? UiLanguage.English : UiLanguage.Thai;
                if (byLanguage.TryGetValue(other, out text) && !string.IsNullOrEmpty(text))
                    return text;
            }

            if (_reportedMissing.Add(key))
            {
                UnityEngine.Debug.LogWarning(
                    $"[ProjectSpy] Missing localization row for '{key}' in {Current}. " +
                    "Rendering the key. Add the row to data/localization.");
            }

            return key;
        }

        /// <summary>Resolves a key and substitutes positional arguments.</summary>
        /// <remarks>
        /// The format string comes from a table row, never from code, so the ordering of a
        /// sentence stays with the translator.
        /// </remarks>
        public string Format(string key, params object[] args)
        {
            string template = Get(key);
            if (args == null || args.Length == 0)
                return template;

            try
            {
                return string.Format(template, args);
            }
            catch (FormatException)
            {
                // A translator typing {0} into a row with no argument is a content bug. Show
                // the template and say so, rather than throwing from a UI binding.
                UnityEngine.Debug.LogWarning(
                    $"[ProjectSpy] Could not format localization key '{key}' with " +
                    $"{args.Length} argument(s); showing the raw template.");
                return template;
            }
        }

        /// <summary>How many keys have rows loaded.</summary>
        public int RowCount => _rows.Count;

        /// <summary>True when a row exists for the key in either language.</summary>
        public bool Has(string key) => key != null && _rows.ContainsKey(key);
    }
}