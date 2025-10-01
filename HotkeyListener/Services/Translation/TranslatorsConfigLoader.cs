using System.IO;
using System.Text.Json;
using HotkeyListener.Services.SystemSpecificStuff.Logging;
using HotkeyListener.Services.Translation.Translators;

namespace HotkeyListener.Services.Translation;

/// <summary>
/// Configuration model for translators_config.json file.
/// </summary>
internal sealed record TranslatorsConfig(
    List<OpenRouterModelConfig> OpenRouterModels
);

/// <summary>
/// Configuration for a single OpenRouter model in JSON.
/// </summary>
internal sealed record OpenRouterModelConfig(
    string ModelId,
    string DisplayName,
    bool IncludeErrorExplanation,
    bool StripReasoningTags
);

/// <summary>
/// Loads translator configuration from translators_config.json file.
/// </summary>
internal static class TranslatorsConfigLoader
{
    private const string ConfigFileName = "translators_config.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Loads OpenRouter configurations from translators_config.json.
    /// Searches for the file starting from the executable directory and walking up parent directories.
    /// </summary>
    /// <returns>List of OpenRouterConfig objects, or empty list if file not found or invalid.</returns>
    public static List<OpenRouterConfig> LoadOpenRouterConfigs()
    {
        var configPath = FindConfigFile();
        if (configPath == null)
        {
            ConsoleLog.Warning($"Config file '{ConfigFileName}' not found. Using default OpenRouter settings.");
            return GetDefaultConfigs();
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<TranslatorsConfig>(json, SerializerOptions);

            if (config?.OpenRouterModels == null || config.OpenRouterModels.Count == 0)
            {
                ConsoleLog.Warning($"Config file '{configPath}' has no OpenRouter models. Using defaults.");
                return GetDefaultConfigs();
            }

            var result = new List<OpenRouterConfig>();
            foreach (var model in config.OpenRouterModels)
            {
                if (string.IsNullOrWhiteSpace(model.ModelId) || string.IsNullOrWhiteSpace(model.DisplayName))
                {
                    ConsoleLog.Warning($"Skipping invalid OpenRouter model config: {JsonSerializer.Serialize(model)}");
                    continue;
                }

                result.Add(new OpenRouterConfig(
                    ModelId: model.ModelId,
                    DisplayName: model.DisplayName,
                    IncludeErrorExplanation: model.IncludeErrorExplanation,
                    StripReasoningTags: model.StripReasoningTags
                ));
            }

            ConsoleLog.Success($"Loaded {result.Count} OpenRouter model(s) from '{configPath}'");
            return result;
        }
        catch (JsonException ex)
        {
            ConsoleLog.Error($"Failed to parse '{configPath}': {ex.Message}. Using defaults.");
            return GetDefaultConfigs();
        }
        catch (IOException ex)
        {
            ConsoleLog.Error($"Failed to read '{configPath}': {ex.Message}. Using defaults.");
            return GetDefaultConfigs();
        }
    }

    private static string? FindConfigFile()
    {
        var directory = AppContext.BaseDirectory;

        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Combine(directory, ConfigFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var parent = Directory.GetParent(directory);
            if (parent == null)
            {
                break;
            }

            directory = parent.FullName;
        }

        return null;
    }

    private static List<OpenRouterConfig> GetDefaultConfigs()
    {
        return new List<OpenRouterConfig>
        {
            new OpenRouterConfig(
                ModelId: "x-ai/grok-4-fast:free",
                DisplayName: "Grok",
                IncludeErrorExplanation: true,
                StripReasoningTags: false),
            new OpenRouterConfig(
                ModelId: "deepseek/deepseek-chat-v3.1:free",
                DisplayName: "DeepSeek",
                IncludeErrorExplanation: false,
                StripReasoningTags: true)
        };
    }
}
