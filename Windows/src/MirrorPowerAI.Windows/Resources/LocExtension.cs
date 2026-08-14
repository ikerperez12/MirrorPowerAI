using System.Windows.Markup;

namespace MirrorPowerAI.Windows.Resources;

/// <summary>
/// Creates a live binding to a localized ResourceManager string.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    /// <summary>Initializes an empty localization extension for XAML.</summary>
    public LocExtension()
    {
    }

    /// <summary>Initializes an extension for a resource key.</summary>
    /// <param name="key">Resource identifier.</param>
    public LocExtension(string key)
    {
        Key = key;
    }

    /// <summary>Gets or sets the resource identifier.</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(Key);
        return new System.Windows.Data.Binding($"[{Key}]")
        {
            Source = LocalizationService.Current,
            Mode = System.Windows.Data.BindingMode.OneWay,
        }.ProvideValue(serviceProvider);
    }
}
