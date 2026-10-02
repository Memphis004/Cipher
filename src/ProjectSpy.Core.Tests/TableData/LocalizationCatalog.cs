using System.Globalization;

namespace ProjectSpy.Core.Tests.TableData;

/// <summary>
/// Loads <c>data/localization/*.csv</c> so the validator can prove every key used by
/// a table exists in every shipped language.
/// </summary>
/// <remarks>
/// Deliberately reads the CSV source rather than the Luban binary: localization is
/// plain text that a translator edits, and the point of the check is to catch a
/// missing key before it ever reaches a build.
/// </remarks>
internal sealed class LocalizationCatalog
{
    private readonly Dictionary<string, string> _byKey;

    private LocalizationCatalog(string language, Dictionary<string, string> byKey)
    {
        Language = language;
        _byKey = byKey;
    }

    /// <summary>Creates a catalog from an already-built map. Used by tests.</summary>
    internal static LocalizationCatalog FromMap(string language, Dictionary<string, string> byKey)
        => new(language, byKey);

    /// <summary>Language code, e.g. <c>th</c>.</summary>
    public string Language { get; }

    /// <summary>Number of entries loaded.</summary>
    public int Count => _byKey.Count;

    /// <summary>True when the key has a non-empty translation.</summary>
    public bool Contains(string key) => _byKey.ContainsKey(key);

    /// <summary>The translation for a key, or an empty string.</summary>
    public string this[string key] => _byKey.TryGetValue(key, out string? value) ? value : string.Empty;

    /// <summary>Every key present, for diffing against the tables.</summary>
    public IReadOnlyCollection<string> Keys => _byKey.Keys;

    /// <summary>Locates the repository root by walking up from the test binaries.</summary>
    internal static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProjectSpy.sln")) &&
                Directory.Exists(Path.Combine(dir.FullName, "data")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repository root above '{AppContext.BaseDirectory}'.");
    }

    /// <summary>Loads one localization CSV.</summary>
    internal static LocalizationCatalog Load(string language, string? repositoryRoot = null)
    {
        string root = repositoryRoot ?? FindRepositoryRoot();
        string path = Path.Combine(root, "data", "localization", language + ".csv");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Localization file for '{language}' is missing at {path}. " +
                "Both th.csv and en.csv must exist.");
        }

        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        int lineNumber = 0;

        foreach (string raw in File.ReadLines(path))
        {
            lineNumber++;

            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("##", StringComparison.Ordinal))
                continue;

            string[] cells = SplitCsvLine(line);
            if (cells.Length < 2)
                continue;


            // Cell 0 is Luban's leading marker column, so the key is cell 1.
            string key = cells[1].Trim();
            string text = cells.Length > 2 ? cells[2].Trim() : string.Empty;

            if (key.Length == 0)
                continue;

            if (byKey.ContainsKey(key))
                throw new InvalidDataException($"Duplicate localization key '{key}' in {language}.csv at line {lineNumber}.");

            byKey[key] = text;
        }

        return new LocalizationCatalog(language, byKey);
    }

    /// <summary>
    /// Splits a CSV line, honouring double-quoted fields and doubled quotes.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than pulled from a package: a handful of lines is cheaper
    /// than a dependency that only the tests would use.
    /// </remarks>
    internal static string[] SplitCsvLine(string line)
    {
        var cells = new List<string>(8);
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // "" inside a quoted field is a literal quote.
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString());
        return cells.ToArray();
    }

    /// <summary>Parses a comma-separated id list from a CSV cell.</summary>
    internal static IReadOnlyList<int> ParseIdList(string cell)
    {
        if (string.IsNullOrWhiteSpace(cell))
            return Array.Empty<int>();

        string[] parts = cell.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<int>(parts.Length);

        foreach (string part in parts)
        {
            string trimmed = part.Trim();
            if (trimmed.Length == 0)
                continue;

            if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                throw new InvalidDataException($"'{trimmed}' is not a valid id in a comma-separated list.");
            }

            result.Add(id);
        }

        return result;
    }

    /// <summary>Splits a comma-separated string list from a CSV cell.</summary>
    internal static IReadOnlyList<string> ParseStringList(string cell)
    {
        if (string.IsNullOrWhiteSpace(cell))
            return Array.Empty<string>();

        string[] parts = cell.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>(parts.Length);

        foreach (string part in parts)
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0)
                result.Add(trimmed);
        }

        return result;
    }
}
