using System.Text.Json;
using System.Text;

internal static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ConfigResult Load()
    {
        var defaults = GetDefaults();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var path = Path.Combine(appData, "NugetUtil", "config.json");

        if (!File.Exists(path))
        {
            var writeResult = TryCreateStarterConfig(path, defaults);
            if (!writeResult.Success)
            {
                return ConfigResult.Fail(writeResult.Error!);
            }

            Console.WriteLine($"Created starter config: {path}");
            return ConfigResult.Ok(defaults);
        }

        try
        {
            var json = File.ReadAllText(path);
            var userConfig = JsonSerializer.Deserialize<NugetUtilConfig>(json, JsonOptions) ?? new NugetUtilConfig();
            var merged = Merge(defaults, userConfig);
            return ConfigResult.Ok(merged);
        }
        catch (Exception ex)
        {
            return ConfigResult.Fail($"Failed reading config '{path}': {ex.Message}");
        }
    }

    public static NugetUtilConfig GetDefaults() => new()
    {
        Behavior = new BehaviorConfig
        {
            SkipDuplicate = true,
            OutputFolder = "artifacts\\nuget",
            ExcludeGlobs = ["**\\bin\\**", "**\\obj\\**", "**\\**Tests**\\**"]
        }
    };

    private static NugetUtilConfig Merge(NugetUtilConfig defaults, NugetUtilConfig user)
    {
        return new NugetUtilConfig
        {
            DefaultSource = string.IsNullOrWhiteSpace(user.DefaultSource) ? defaults.DefaultSource : user.DefaultSource,
            Sources = user.Sources.Count == 0 ? defaults.Sources : new Dictionary<string, SourceConfig>(user.Sources, StringComparer.OrdinalIgnoreCase),
            Behavior = new BehaviorConfig
            {
                SkipDuplicate = user.Behavior.SkipDuplicate ?? defaults.Behavior.SkipDuplicate,
                OutputFolder = string.IsNullOrWhiteSpace(user.Behavior.OutputFolder) ? defaults.Behavior.OutputFolder : user.Behavior.OutputFolder,
                ExcludeGlobs = user.Behavior.ExcludeGlobs.Count == 0 ? defaults.Behavior.ExcludeGlobs : user.Behavior.ExcludeGlobs
            }
        };
    }

    private static WriteConfigResult TryCreateStarterConfig(string path, NugetUtilConfig defaults)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return WriteConfigResult.Fail($"Invalid config path: {path}");
            }

            Directory.CreateDirectory(folder);

            var starter = new
            {
                defaultSource = "MyFeed",
                sources = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MyFeed"] = new
                    {
                        apiKey = "REPLACE_ME"
                    }
                },
                behavior = new
                {
                    skipDuplicate = defaults.Behavior.SkipDuplicate ?? true,
                    outputFolder = defaults.Behavior.OutputFolder ?? "artifacts\\nuget",
                    excludeGlobs = defaults.Behavior.ExcludeGlobs
                }
            };

            var json = JsonSerializer.Serialize(starter, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return WriteConfigResult.Ok();
        }
        catch (Exception ex)
        {
            return WriteConfigResult.Fail($"Failed creating starter config '{path}': {ex.Message}");
        }
    }
}

internal sealed record WriteConfigResult(bool Success, string? Error)
{
    public static WriteConfigResult Ok() => new(true, null);
    public static WriteConfigResult Fail(string error) => new(false, error);
}

internal sealed record ConfigResult(bool Success, NugetUtilConfig? Config, string? Error)
{
    public static ConfigResult Ok(NugetUtilConfig config) => new(true, config, null);
    public static ConfigResult Fail(string error) => new(false, null, error);
}

internal sealed class NugetUtilConfig
{
    public string? DefaultSource { get; init; }
    public Dictionary<string, SourceConfig> Sources { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public BehaviorConfig Behavior { get; init; } = new();
}

internal sealed class SourceConfig
{
    public string ApiKey { get; init; } = string.Empty;
}

internal sealed class BehaviorConfig
{
    public bool? SkipDuplicate { get; init; }
    public string? OutputFolder { get; init; }
    public List<string> ExcludeGlobs { get; init; } = [];
}
