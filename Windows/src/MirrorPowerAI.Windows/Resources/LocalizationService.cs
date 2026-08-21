using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace MirrorPowerAI.Windows.Resources;

/// <summary>
/// Provides live ResourceManager-backed strings to WPF bindings.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly ResourceManager ResourceManager =
        new("MirrorPowerAI.Windows.Resources.Strings", typeof(LocalizationService).Assembly);
    private readonly CultureInfo _systemCulture = CultureInfo.CurrentUICulture;
    private CultureInfo _culture = CultureInfo.GetCultureInfo("es");

    private LocalizationService()
    {
    }

    /// <summary>Gets the process-wide localization service.</summary>
    public static LocalizationService Current { get; } = new();

    /// <summary>Gets a localized string by resource key.</summary>
    /// <param name="key">Resource identifier.</param>
    /// <returns>The localized value, or the key in brackets when missing.</returns>
    public string this[string key] => ResourceManager.GetString(key, _culture) ?? $"[{key}]";

    /// <summary>Gets the BCP 47 language tag used by WPF and UI Automation elements.</summary>
    public string UiLanguage => _culture.IetfLanguageTag;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Selects Spanish, English, or automatic language detection and refreshes WPF bindings.
    /// </summary>
    /// <param name="language">The persisted language identifier.</param>
    public void SetLanguage(string? language)
    {
        var requestedCulture = string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)
            ? _systemCulture
            : CultureInfo.GetCultureInfo(string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "es");
        _culture = requestedCulture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("en")
            : CultureInfo.GetCultureInfo("es");

        CultureInfo.DefaultThreadCurrentUICulture = _culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiLanguage)));
    }
}
