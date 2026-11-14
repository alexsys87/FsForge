using System.Globalization;

namespace FsForge.Presentation.Services;

/// <summary>Language of the interface. Messages of the core (reading and writing formats) stay in English.</summary>
public enum AppLanguage
{
    /// <summary>Russian when Windows is set to Russian, English otherwise.</summary>
    System,
    Russian,
    English,
}

/// <summary>
/// Interface strings. The Russian text is the key: it is shown as it is in Russian, and looked up in
/// <see cref="EnglishStrings"/> in English. A text without a translation is shown in Russian, so a
/// missing entry never hides a message (tests/check-translations.py finds such entries).
/// The language is chosen once at startup; changing it takes effect after a restart.
/// </summary>
public static class L
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    /// <summary>True when the interface is in English.</summary>
    public static bool IsEnglish { get; private set; }

    /// <summary>Culture for numbers in the interface: "12,3" and "1 234" in Russian, "12.3" and "1,234" in English.</summary>
    public static CultureInfo Culture => IsEnglish ? En : Ru;

    /// <summary>Applies the language setting. Call before any window or view model is created.</summary>
    public static void Set(AppLanguage language) => IsEnglish = Resolve(language) == AppLanguage.English;

    /// <summary>The language actually used for a setting: <see cref="AppLanguage.System"/> becomes Russian or English.</summary>
    public static AppLanguage Resolve(AppLanguage language) => language switch
    {
        AppLanguage.Russian => AppLanguage.Russian,
        AppLanguage.English => AppLanguage.English,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? AppLanguage.Russian : AppLanguage.English,
    };

    /// <summary>Translates a Russian interface text.</summary>
    public static string T(string russian) =>
        IsEnglish && EnglishStrings.Map.TryGetValue(russian, out string? english) ? english : russian;

    /// <summary>Translates a Russian interface text into the given language, whatever the current one is.</summary>
    public static string T(string russian, AppLanguage language) =>
        Resolve(language) == AppLanguage.English && EnglishStrings.Map.TryGetValue(russian, out string? english) ? english : russian;

    /// <summary>Translates a composite format string ("Выбрано: {0} из {1}") and fills it in.</summary>
    public static string F(string russian, params object?[] args) => string.Format(Culture, T(russian), args);

    /// <summary>
    /// "5 файлов" / "5 files". The Russian forms are for 1, 2–4 and 5+ items; the English entry for
    /// "одна|две|пять" holds "one|many".
    /// </summary>
    public static string Plural(long n, string one, string few, string many)
    {
        string number = n.ToString("#,0", Culture);
        if (IsEnglish && EnglishStrings.Map.TryGetValue(one + "|" + few + "|" + many, out string? english))
        {
            string[] forms = english.Split('|');
            return $"{number} {(Math.Abs(n) == 1 || forms.Length < 2 ? forms[0] : forms[1])}";
        }
        long a = Math.Abs(n) % 100, b = a % 10;
        string word = a is >= 11 and <= 14 ? many : b == 1 ? one : b is >= 2 and <= 4 ? few : many;
        return $"{number} {word}";
    }
}
