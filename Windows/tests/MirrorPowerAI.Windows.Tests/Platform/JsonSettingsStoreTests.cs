using MirrorPowerAI.Windows.Platform;

namespace MirrorPowerAI.Windows.Tests.Platform;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        "MirrorPowerAI.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoadAsync_ContextIsNeverSerializedAndOtherSettingsRoundTrip()
    {
        // Arrange
        var path = Path.Combine(_testDirectory, "settings.json");
        var store = new JsonSettingsStore(path);
        var settings = new AppSettings
        {
            Context = "PRIVATE PROJECT CONTEXT",
            TranscriptionProvider = TranscriptionProviders.GeminiAudio,
            Language = "EN",
            AudioDeviceId = "  endpoint-42  ",
            GeminiAudioConsentVersion = 2,
        };

        // Act
        await store.SaveAsync(settings);
        var json = await File.ReadAllTextAsync(path);
        var loaded = await store.LoadAsync();

        // Assert
        Assert.DoesNotContain("PRIVATE PROJECT CONTEXT", json, StringComparison.Ordinal);
        Assert.DoesNotContain("context", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, loaded.Context);
        Assert.Equal(TranscriptionProviders.GeminiAudio, loaded.TranscriptionProvider);
        Assert.Equal("en", loaded.Language);
        Assert.Equal("endpoint-42", loaded.AudioDeviceId);
        Assert.Equal(2, loaded.GeminiAudioConsentVersion);
    }

    [Fact]
    public async Task SaveAndLoadAsync_InvalidValuesAreNormalizedToLocalSafeDefaults()
    {
        // Arrange
        var path = Path.Combine(_testDirectory, "settings.json");
        var store = new JsonSettingsStore(path);
        var settings = new AppSettings
        {
            TranscriptionProvider = "UnknownCloudProvider",
            Language = "invalid",
            AudioDeviceId = "   ",
            GeminiAudioConsentVersion = 99,
        };

        // Act
        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        // Assert
        Assert.Equal(TranscriptionProviders.LocalWhisper, loaded.TranscriptionProvider);
        Assert.Equal("es", loaded.Language);
        Assert.Equal(AudioDeviceOption.DefaultDeviceId, loaded.AudioDeviceId);
        Assert.Equal(0, loaded.GeminiAudioConsentVersion);
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_ReturnsDefaults()
    {
        // Arrange
        Directory.CreateDirectory(_testDirectory);
        var path = Path.Combine(_testDirectory, "settings.json");
        await File.WriteAllTextAsync(path, "{ definitely-not-json");
        var store = new JsonSettingsStore(path);

        // Act
        var loaded = await store.LoadAsync();

        // Assert
        Assert.Equal(new AppSettings(), loaded);
    }

    [Fact]
    public async Task LoadAsync_OversizedValidJson_ReturnsDefaultsWithoutDeserializing()
    {
        // Arrange: without the size gate this valid JSON would select the cloud provider.
        Directory.CreateDirectory(_testDirectory);
        var path = Path.Combine(_testDirectory, "settings.json");
        var oversizedJson =
            "{\"transcriptionProvider\":\"GeminiAudio\",\"language\":\"en\",\"padding\":\""
            + new string('x', checked((int)JsonSettingsStore.MaximumSettingsFileBytes))
            + "\"}";
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(oversizedJson)
            > JsonSettingsStore.MaximumSettingsFileBytes);
        await File.WriteAllTextAsync(path, oversizedJson);
        var store = new JsonSettingsStore(path);

        // Act
        var loaded = await store.LoadAsync();

        // Assert
        Assert.Equal(new AppSettings(), loaded);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
