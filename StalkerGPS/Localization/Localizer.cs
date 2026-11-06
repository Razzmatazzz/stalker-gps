using System.Globalization;
using System.Text.Json;
using StalkerGPS.Services;

namespace StalkerGPS.Localization;

public sealed record Language(string Code, string Name);

/// <summary>
/// The app's current language: the one chosen in settings, otherwise the closest match to Windows'
/// display language, otherwise English. Codes match the stalker-vodka website (app/i18n.ts).
/// </summary>
public sealed class Localizer
{
    private const string English = "en";

    public static readonly IReadOnlyList<Language> Languages =
    [
        new("ar", "العربية"),
        new("cs", "Čeština"),
        new("de", "Deutsch"),
        new(English, "English"),
        new("es-419", "Español (Latinoamérica)"),
        new("es", "Español"),
        new("fr", "Français"),
        new("it", "Italiano"),
        new("ja", "日本語"),
        new("ko", "한국어"),
        new("pl", "Polski"),
        new("pt-BR", "Português (Brasil)"),
        new("ru", "Русский"),
        new("sr", "Srpski"),
        new("tr", "Türkçe"),
        new("uk", "Українська"),
        new("zh-Hans", "简体中文"),
        new("zh-Hant", "繁體中文"),
    ];

    private readonly SettingsStore _settings;

    public Strings T { get; private set; } = new();

    public string Code { get; private set; } = English;

    /// <summary>For numbers: the app's language, not Windows' regional format.</summary>
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(English);

    public bool IsRightToLeft => Culture.TextInfo.IsRightToLeft;

    /// <summary>Raised on the caller's thread when the language changes.</summary>
    public event Action? Changed;

    public Localizer(SettingsStore settings)
    {
        _settings = settings;
        _settings.Saved += Apply;
        Apply();
    }

    public string Number(double value, string format = "N0") => value.ToString(format, Culture);

    /// <summary>Keeps left-to-right text such as a path in order inside right-to-left text.</summary>
    public static string Isolated(string text) => $"⁦{text}⁩";

    private void Apply()
    {
        string code = Languages.Any(l => l.Code == _settings.Current.Language)
            ? _settings.Current.Language!
            : Match(CultureInfo.CurrentUICulture);
        if (code == Code)
            return;

        Code = code;
        T = Load(code);
        // Srpski is written in Latin script; plain "sr" is Cyrillic in .NET.
        Culture = CultureInfo.GetCultureInfo(code == "sr" ? "sr-Latn" : code);
        Changed?.Invoke();
    }

    private static string Match(CultureInfo culture)
    {
        string language = culture.TwoLetterISOLanguageName;
        return language switch
        {
            "es" => culture.Name is "es" or "es-ES" ? "es" : "es-419",
            "pt" => "pt-BR",
            // zh-TW and zh-HK have zh-Hant as a parent.
            "zh" => IsOrInherits(culture, "zh-Hant") ? "zh-Hant" : "zh-Hans",
            _ => Languages.Any(l => l.Code == language) ? language : English,
        };
    }

    private static bool IsOrInherits(CultureInfo culture, string name)
    {
        for (var c = culture; c.Name.Length > 0; c = c.Parent)
        {
            if (c.Name == name)
                return true;
        }
        return false;
    }

    private static Strings Load(string code)
    {
        if (code == English)
            return new Strings();
        using var stream = typeof(Localizer).Assembly.GetManifestResourceStream($"StalkerGPS.Localization.{code}.json")!;
        return JsonSerializer.Deserialize<Strings>(stream) ?? new Strings();
    }
}
