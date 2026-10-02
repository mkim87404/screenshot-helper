using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ScreenshotHelper.Core.Diagnostics;
using ScreenshotHelper.Core.IO;

namespace ScreenshotHelper.Core.Settings;

/// <summary>Source-generated JSON metadata (trim/AOT-safe, no reflection).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AppState))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> and <see cref="AppState"/> as JSON files, written atomically.
/// A corrupt file is moved aside and replaced by defaults instead of crashing the app.
/// </summary>
public sealed class SettingsStore
{
    private readonly AppLog _log;

    public SettingsStore(string directory, AppLog log)
    {
        Directory = directory;
        _log = log;
    }

    public string Directory { get; }

    public string SettingsPath => Path.Combine(Directory, "settings.json");

    public string StatePath => Path.Combine(Directory, "state.json");

    public AppSettings LoadSettings() =>
        (Load(SettingsPath, SettingsJsonContext.Default.AppSettings, AppSettings.Default) ?? AppSettings.Default).Sanitized();

    public AppState LoadState() =>
        (Load(StatePath, SettingsJsonContext.Default.AppState, new AppState()) ?? new AppState()).Sanitized();

    public void Save(AppSettings settings) =>
        AtomicFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));

    public void Save(AppState state) =>
        AtomicFile.WriteAllText(StatePath, JsonSerializer.Serialize(state, SettingsJsonContext.Default.AppState));

    /// <summary>Reads a JSON file over <paramref name="defaults"/>: keys missing from the file keep their default values.</summary>
    private T? Load<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, T defaults)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is null)
            {
                return null;
            }

            if (node is not JsonObject loaded)
            {
                throw new JsonException($"Expected a JSON object in {path}.");
            }

            // .NET 10's source-generated deserializer gives an init-only property that's missing from the JSON its type's default
            // (false, 0, null) instead of its initializer (dotnet/runtime#84484, fixed in .NET 11). So start from the defaults and lay
            // the file's keys over them: a setting added in a newer version, or left out of a hand-edited file, keeps its default.
            var merged = JsonSerializer.SerializeToNode(defaults, typeInfo)!.AsObject();
            foreach (var (key, value) in loaded)
            {
                merged[key] = value?.DeepClone();
            }

            return merged.Deserialize(typeInfo);
        }
        catch (JsonException ex)
        {
            QuarantineCorrupt(path, ex);
            return null;
        }
        catch (IOException ex)
        {
            _log.Warn($"Could not read {path}; using defaults.", ex);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn($"Could not read {path}; using defaults.", ex);
            return null;
        }
    }

    private void QuarantineCorrupt(string path, Exception ex)
    {
        var backup = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
        _log.Warn($"{path} is not valid JSON; moved to {backup} and using defaults.", ex);
        try
        {
            File.Move(path, backup, overwrite: true);
        }
        catch (IOException moveEx)
        {
            _log.Warn($"Could not move corrupt {path} aside.", moveEx);
        }
        catch (UnauthorizedAccessException moveEx)
        {
            _log.Warn($"Could not move corrupt {path} aside.", moveEx);
        }

        PruneCorruptBackups(path);
    }

    /// <summary>Keeps only the newest few backups of a corrupt file, so a repeatedly broken file can't accumulate copies.</summary>
    private static void PruneCorruptBackups(string path, int keep = 3)
    {
        var directory = Path.GetDirectoryName(path)!;
        var stale = System.IO.Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".corrupt-*")
            .Order(StringComparer.Ordinal)
            .SkipLast(keep);
        foreach (var backup in stale)
        {
            AtomicFile.TryDelete(backup);
        }
    }
}
