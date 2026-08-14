using System.IO;
using System.Text.Json;

namespace MirrorPowerAI.Windows.Platform;

/// <summary>
/// Persists non-secret settings as bounded JSON under the current user's local application data.
/// </summary>
public sealed class JsonSettingsStore
{
    /// <summary>Maximum accepted JSON size, protecting startup from manipulated local files.</summary>
    public const long MaximumSettingsFileBytes = 64 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    /// <summary>Initializes the store in <c>%LOCALAPPDATA%\MirrorPowerAI</c>.</summary>
    public JsonSettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MirrorPowerAI",
            "settings.json"))
    {
    }

    /// <summary>Initializes a store at an explicit path, primarily for isolated tests.</summary>
    /// <param name="settingsPath">Absolute settings file path.</param>
    public JsonSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    /// <summary>Loads settings, returning safe defaults for a missing or malformed file.</summary>
    /// <param name="cancellationToken">Cancels file I/O.</param>
    /// <returns>Normalized application settings.</returns>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = new FileStream(
                _settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumSettingsFileBytes)
            {
                return new AppSettings();
            }

            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            return (settings ?? new AppSettings()).Normalize();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Atomically persists normalized non-secret settings.</summary>
    /// <param name="settings">Settings to persist.</param>
    /// <param name="cancellationToken">Cancels file I/O.</param>
    /// <returns>A task that completes once the replacement is durable.</returns>
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("The settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    settings.Normalize(),
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
