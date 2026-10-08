using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Composa.App;

/// <summary>
/// The one place a user-visible English name becomes the words the person reads.
///
/// English is the source of truth and the identity: history step names, shortcut ids, blend mode
/// names and every string an MCP agent sends or receives stay English. Only at the border where a
/// string is about to be drawn does it pass through here, so switching the language cannot change
/// what the document stores, what a rebound key is keyed by, which icon a history step picks, or
/// what an agent must send. A language with no translation falls back to the English neutral
/// resource, and an unknown string is returned as it came in, so a missing entry reads as English
/// rather than as nothing.
/// </summary>
public static class Loc
{
    /// <summary>The neutral language: names are shown exactly as the code writes them.</summary>
    public const string SystemLanguage = "auto";
    public const string English = "en";

    private static readonly ResourceManager Strings = new("Composa.App.Strings", typeof(Loc).Assembly);
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    /// <summary>
    /// Each culture's resource keys folded to one spelling per name. GenerateResource treats names
    /// that differ only in case as duplicates and drops one of them, and the same English word does
    /// arrive in two cases - the tool is "Blur", the smear's verb is "blur" - so a lookup that only
    /// matched exactly would silently lose its translation for one of the two. This maps either
    /// spelling onto whichever the resource carries.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> Folded = new();

    private static Dictionary<string, string> Fold(CultureInfo culture) => Folded.GetOrAdd(culture.Name, _ =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var set = Strings.GetResourceSet(culture, createIfNotExists: true, tryParents: true);
        if (set == null) return map;
        foreach (DictionaryEntry entry in set)
            if (entry.Key is string key && key.Length > 0) map.TryAdd(key, key);
        return map;
    });

    /// <summary>The languages offered, each with the name it calls itself.</summary>
    public static readonly (string Code, string Name)[] Available =
    [
        (SystemLanguage, "Follow the system"),
        (English, "English"),
        ("zh-CN", "简体中文"),
    ];

    /// <summary>The language setting: "auto", "en", or a culture code from <see cref="Available"/>.</summary>
    public static string Setting { get; private set; } = SystemLanguage;

    /// <summary>The culture actually drawing text, resolved from the setting and the operating system.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(English);

    /// <summary>Whether the drawing language is other than English, which is what tells the app to widen its layout.</summary>
    public static bool IsLocalised => !Culture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Applies a stored language setting, resolving "auto" against the system UI culture. A host that
    /// does not persist settings is a test or another program driving the editor rather than a person,
    /// and it stays in English: the names in the code are what those hosts assert against.
    /// </summary>
    public static void Apply(string? setting)
    {
        Setting = string.IsNullOrWhiteSpace(setting) ? SystemLanguage : setting!;
        var code = Setting switch
        {
            SystemLanguage => Settings.Persist ? CultureInfo.CurrentUICulture.Name : English,
            English => English,
            _ => Setting,
        };
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(code) ? English : code); }
        catch (CultureNotFoundException) { culture = CultureInfo.GetCultureInfo(English); }
        // A system language the repository ships no resource for still reads as English rather than as
        // a half-translated interface, so the culture is only kept when its own resource set exists.
        // tryParents stays off: falling back to the neutral set is not a translation. The set has to be
        // asked for with createIfNotExists on, since a resource not loaded yet reports as missing.
        if (!culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            && Strings.GetResourceSet(culture, createIfNotExists: true, tryParents: false) == null)
            culture = CultureInfo.GetCultureInfo(English);
        Culture = culture;
        Cache.Clear();
    }

    /// <summary>The words for an English name in the language being drawn; the name itself when nothing is translated.</summary>
    public static string T(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (Culture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase)) return name;
        return Cache.GetOrAdd(name, k => Look(k));
    }

    /// <summary>
    /// <see cref="T"/> for a place that shares an English word with another and needs different
    /// words: "Light" is a Camera Raw panel and a dither color swatch. The entry named
    /// <c>Name@Context</c> wins and <c>Name</c> is the fallback, so one resource carries both readings
    /// and English output never changes. Its own name, rather than an overload of <see cref="T"/>,
    /// keeps <c>Loc.T</c> usable as a method group where a label function is wanted.
    /// </summary>
    public static string In(string name, string context)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (Culture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase)) return name;
        return Cache.GetOrAdd(name + "@" + context, k => Look(k, context));
    }

    private static string Look(string name, string? context = null)
    {
        try
        {
            // A context-specific entry wins over the plain one, so a shared English word can read
            // differently in two panels without either losing its own wording.
            if (context != null)
            {
                var scoped = Strings.GetString(name + "@" + context, Culture);
                if (!string.IsNullOrEmpty(scoped)) return scoped;
            }
            // Exact first: a header that carries its accelerator marker is filed under that spelling.
            var value = Strings.GetString(name, Culture);
            if (!string.IsNullOrEmpty(value)) return value;
            var map = Fold(Culture);
            // Then the same name with Avalonia's accelerator markers taken out, so one entry serves a
            // name whether or not the caller wrote it with a marker. "__" is a literal underscore.
            var bare = name.Replace("__", "\u0000").Replace("_", "").Replace("\u0000", "_");
            foreach (var candidate in name.Equals(bare, StringComparison.Ordinal) ? new[] { name } : new[] { name, bare })
            {
                if (map.TryGetValue(candidate, out var actual) && !ReferenceEquals(actual, candidate))
                {
                    value = Strings.GetString(actual, Culture);
                    if (!string.IsNullOrEmpty(value)) return value;
                }
            }
            return name;
        }
        catch (MissingManifestResourceException) { return name; }
    }

    /// <summary>A translated sentence with placeholders filled, so no translation has to reassemble word order.</summary>
    public static string Format(string key, params object?[] args)
    {
        var text = T(key);
        try { return string.Format(Culture, text, args); }
        catch (FormatException) { return text; }
    }
}
