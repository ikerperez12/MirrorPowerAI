using System.ComponentModel;
using System.Xml.Linq;
using MirrorPowerAI.Windows.Resources;
using MirrorPowerAI.Windows.Tests.Platform;

namespace MirrorPowerAI.Windows.Tests.Accessibility;

[Collection(nameof(WpfSettingsWindowSerialTestSuite))]
public sealed class LocalizationAccessibilityContractTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace AutomationNamespace =
        "clr-namespace:System.Windows.Automation;assembly=PresentationCore";

    private const string UiLanguageBinding =
        "{Binding UiLanguage, Source={x:Static loc:LocalizationService.Current}, ConverterCulture=en-US}";

    [Fact]
    public void SetLanguage_ChangesUiLanguageAndRefreshesLocalizedBindings()
    {
        var localization = LocalizationService.Current;
        var originalLanguage = localization.UiLanguage;
        var notifications = new List<string?>();
        PropertyChangedEventHandler handler = (_, eventArgs) => notifications.Add(eventArgs.PropertyName);
        localization.PropertyChanged += handler;

        try
        {
            localization.SetLanguage("en");

            Assert.Equal("en", localization.UiLanguage);
            Assert.Equal("Gemini Audio consent", localization["CloudConsentLabel"]);
            Assert.Equal(
                "Per-monitor DPI awareness could not be enabled. The interface may not scale correctly.",
                localization["DpiAwarenessFailed"]);
            Assert.Contains("Item[]", notifications);
            Assert.Contains(nameof(LocalizationService.UiLanguage), notifications);

            localization.SetLanguage("es");

            Assert.Equal("es", localization.UiLanguage);
            Assert.Equal("Consentimiento para Gemini Audio", localization["CloudConsentLabel"]);
            Assert.Equal(
                "No se pudo activar la compatibilidad de DPI por monitor. La interfaz podría no escalarse correctamente.",
                localization["DpiAwarenessFailed"]);
            Assert.Equal("MirrorPowerAI está capturando audio.", localization["TrayAnnouncementCapturing"]);
        }
        finally
        {
            localization.PropertyChanged -= handler;
            localization.SetLanguage(originalLanguage);
        }
    }

    [Fact]
    public void Windows_UseLiveUiLanguageAndMaterialConsentTextForAutomation()
    {
        var mainWindow = LoadXaml("Windows", "src", "MirrorPowerAI.Windows", "MainWindow.xaml");
        var overlayWindow = LoadXaml("Windows", "src", "MirrorPowerAI.Windows", "UI", "OverlayWindow.xaml");
        var consent = mainWindow
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "CheckBox" &&
                AttributeValue(element, XamlNamespace + "Name") == "CloudConsentBox");
        var status = mainWindow
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "TextBlock" &&
                AttributeValue(element, XamlNamespace + "Name") == "StatusText");
        var question = overlayWindow
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "TextBox" &&
                AttributeValue(element, XamlNamespace + "Name") == "QuestionTextBox");
        var answer = overlayWindow
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "TextBox" &&
                AttributeValue(element, XamlNamespace + "Name") == "AnswerTextBox");

        Assert.Equal(UiLanguageBinding, AttributeValue(mainWindow.Root!, "Language"));
        Assert.Equal(UiLanguageBinding, AttributeValue(overlayWindow.Root!, "Language"));
        Assert.Equal(
            "{loc:Loc CloudConsentText}",
            AttributeValue(consent, AutomationNamespace + "AutomationProperties.Name"));
        Assert.Equal(
            "{loc:Loc CloudConsentText}",
            AttributeValue(consent, AutomationNamespace + "AutomationProperties.HelpText"));
        Assert.Equal("False", AttributeValue(status, "Focusable"));
        Assert.Equal(
            "Assertive",
            AttributeValue(status, AutomationNamespace + "AutomationProperties.LiveSetting"));
        Assert.Equal(
            "Polite",
            AttributeValue(question, AutomationNamespace + "AutomationProperties.LiveSetting"));
        Assert.Equal(
            "Polite",
            AttributeValue(answer, AutomationNamespace + "AutomationProperties.LiveSetting"));
    }

    [Fact]
    public void App_UsesDedicatedLocalizedMessageWhenDpiAwarenessFails()
    {
        var appSource = LoadSource("Windows", "src", "MirrorPowerAI.Windows", "App.xaml.cs");

        Assert.Matches(
            @"if \(!dpiResult\.IsUsable\)\s*\{\s*_trayIcon\.ShowError\(localization\[""DpiAwarenessFailed""\]\);\s*\}",
            appSource);
    }

    [Fact]
    public void LocalizedResources_ExposeTheSameNonEmptyKeys()
    {
        // Arrange
        var spanish = LoadResources("Windows", "src", "MirrorPowerAI.Windows", "Resources", "Strings.resx");
        var english = LoadResources("Windows", "src", "MirrorPowerAI.Windows", "Resources", "Strings.en.resx");

        // Assert
        Assert.Equal(spanish.Keys.Order(), english.Keys.Order());
        Assert.All(spanish, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), pair.Key));
        Assert.All(english, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), pair.Key));
    }

    private static XDocument LoadXaml(params string[] relativePath)
    {
        return XDocument.Load(FindRepositoryFile(relativePath));
    }

    private static string LoadSource(params string[] relativePath)
    {
        return File.ReadAllText(FindRepositoryFile(relativePath));
    }

    private static Dictionary<string, string> LoadResources(params string[] relativePath)
    {
        return XDocument.Load(FindRepositoryFile(relativePath))
            .Descendants("data")
            .ToDictionary(
                element => element.Attribute("name")?.Value
                    ?? throw new InvalidDataException("A localized resource does not have a name."),
                element => element.Element("value")?.Value
                    ?? throw new InvalidDataException("A localized resource does not have a value."),
                StringComparer.Ordinal);
    }

    private static string FindRepositoryFile(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("The repository source file was not found.", Path.Combine(relativePath));
    }

    private static string? AttributeValue(XElement element, XName name) => element.Attribute(name)?.Value;

    private static string? AttributeValue(XElement element, string localName) =>
        AttributeValue(element, XName.Get(localName));
}
