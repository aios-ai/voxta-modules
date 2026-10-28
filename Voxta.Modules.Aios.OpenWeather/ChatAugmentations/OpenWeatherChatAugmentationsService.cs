using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Abstractions.Encryption;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Abstractions.Services;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Modules.Aios.OpenWeather.Clients;
using Voxta.Modules.Aios.OpenWeather.Configuration;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using Voxta.Modules.Aios.OpenWeather.Tools;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.OpenWeather.ChatAugmentations;

public class OpenWeatherChatAugmentationsService(
    IOpenWeatherClientFactory clientFactory,
    ILocalEncryptionProvider localEncryptionProvider,
    ILoggerFactory loggerFactory
) : ServiceBase(loggerFactory.CreateLogger<OpenWeatherChatAugmentationsService>()), IToolsChatAugmentationsService
{
    public Task<AugmentationPlanItem[]> PlanAsync(
        IChatAugmentationPlanContext context,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult<AugmentationPlanItem[]>(
        [
            CreateToolsPlan(),
            VoxtaModule.Augmentations.Get(VoxtaModule.AugmentationKey).Plan(
                AugmentationActivation.OptIn,
                CreateOpenWeatherChatAugmentationsServiceInstance
            ),
        ]);
    }

    private AugmentationPlanItem CreateToolsPlan()
    {
        var plan = VoxtaModule.Augmentations.Get(VoxtaModule.ToolsAugmentationKey).Plan(
            AugmentationActivation.OptIn, (session, token) => CreateInstance(session, token, tools: true));
        return new AugmentationPlanItem
        {
            Key = plan.Key, Label = plan.Label, Description = plan.Description,
            Activation = plan.Activation, ActivateAsync = plan.ActivateAsync, IncludeInCatchAll = false,
            Tools = OpenWeatherToolCatalog.Choices.Select(choice => new AugmentationToolDeclaration(choice.Name, VoxtaModule.ServiceName)).ToArray(),
        };
    }

    private Task<IChatAugmentationServiceInstanceBase?> CreateOpenWeatherChatAugmentationsServiceInstance(
        IChatSessionChatAugmentationApi session,
        CancellationToken cancellationToken) => CreateInstance(session, cancellationToken, tools: false);

    private Task<IChatAugmentationServiceInstanceBase?> CreateInstance(
        IChatSessionChatAugmentationApi session, CancellationToken cancellationToken, bool tools)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Explicit tools take precedence if both are selected, avoiding duplicate provider requests and replies.
        if (!tools && session.IsAugmentationEnabled(VoxtaModule.ToolsAugmentationKey, includeInCatchAll: false)
            && !session.IsAugmentationSuppressed(VoxtaModule.ToolsAugmentationKey))
            return Task.FromResult<IChatAugmentationServiceInstanceBase?>(null);
        var logger = loggerFactory.CreateLogger<OpenWeatherChatAugmentationsServiceInstance>();
        string apiKey;
        try
        {
            apiKey = localEncryptionProvider.Decrypt(ModuleConfiguration.GetRequired(ModuleConfigurationProvider.ApiKey));
        }
        catch (Exception exc)
        {
            logger.LogError(exc, "OpenWeather API key configuration could not be loaded");
            apiKey = string.Empty;
        }

        var client = clientFactory.CreateClient(apiKey);
        var rawSelectedWeather = GetSettingOrDefault(
            () => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.WeatherDetails),
            Array.Empty<string>(),
            "WeatherDetails",
            logger);
        var rawSelectedPollution = GetSettingOrDefault(
            () => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.PollutionDetails),
            Array.Empty<string>(),
            "PollutionDetails",
            logger);
        var selectedWeather = ParseKeys(rawSelectedWeather, new[] { "Temp" });
        var selectedPollution = ParseKeys(rawSelectedPollution, new[] { "AQI" });
        var rawTileCachePath = GetSettingOrDefault(
            () => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.TileCachePath),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voxta", "Aios.OpenWeather"),
            "TileCachePath",
            logger);
        var tileCachePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rawTileCachePath));
        var actions = tools ? [] : GetSettingOrDefault(
            () => ModuleConfiguration.GetOptional<OpenWeatherActionSettings>(ModuleConfigurationProvider.Actions),
            ModuleConfigurationProvider.DefaultActions,
            "Actions",
            logger);
        var config = new OpenWeatherChatAugmentationsSettings
        {
            Actions = NormalizeActions(actions),
            MyLocation = GetSettingOrDefault(() => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.MyLocation), string.Empty, "MyLocation", logger),
            Units = GetSettingOrDefault(() => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.Units), "metric", "Units", logger),
            WeatherDetails = selectedWeather.ToArray(),
            PollutionDetails = selectedPollution.ToArray(),
            TileCachePath = tileCachePath,
        };
        if (tools)
        {
            var handler = new OpenWeatherToolHandler(client, config, session.MainCharacter.Culture, session.Abort);
            return Task.FromResult<IChatAugmentationServiceInstanceBase?>(new ModuleToolsAugmentation(
                OpenWeatherToolCatalog.Create(handler, OpenWeatherToolsSettings.From(ModuleConfiguration), logger)));
        }
        logger.LogInformation("Chat session {SessionId} has been augmented with {Augmentation}", session.SessionId, VoxtaModule.AugmentationKey);
        return Task.FromResult<IChatAugmentationServiceInstanceBase?>(
            new OpenWeatherChatAugmentationsServiceInstance(session, client, config, logger));
    }

    private static OpenWeatherActionSettings[] NormalizeActions(OpenWeatherActionSettings[]? actions)
    {
        if (actions is not { Length: > 0 })
            return ModuleConfigurationProvider.DefaultActions;

        var defaultsByName = ModuleConfigurationProvider.DefaultActions.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        return actions
            .Where(action => !string.IsNullOrWhiteSpace(action.Name) && defaultsByName.ContainsKey(action.Name))
            .Select(action =>
            {
                var defaultAction = defaultsByName[action.Name];
                return new OpenWeatherActionSettings
                {
                    Name = defaultAction.Name,
                    Layer = defaultAction.Layer,
                    ShortDescription = string.IsNullOrWhiteSpace(action.ShortDescription) ? defaultAction.ShortDescription : action.ShortDescription,
                    Description = string.IsNullOrWhiteSpace(action.Description) ? defaultAction.Description : action.Description,
                    MatchFilter = NormalizeMatchFilter(action.MatchFilter, defaultAction.MatchFilter),
                    Disabled = action.Disabled,
                    CancelReply = action.CancelReply,
                };
            })
            .ToArray();
    }

    private static string? NormalizeMatchFilter(string? matchFilter, string? fallback)
    {
        var values = (matchFilter ?? "")
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return values.Length == 0 ? fallback : string.Join(Environment.NewLine, values);
    }

    public static bool ParseActionBoolean(string? value, bool fallback)
    {
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }
    
    private static HashSet<string> ParseKeys(string[]? raw, IEnumerable<string> defaults)
    {
        var keys = new HashSet<string>(
            (raw ?? Array.Empty<string>())
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s)),
            StringComparer.OrdinalIgnoreCase);
        
        if (keys.Count == 0)
            return defaults.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return keys;
    }

    private static T GetSettingOrDefault<T>(
        Func<T> read,
        T fallback,
        string settingName,
        ILogger logger)
    {
        try
        {
            return read();
        }
        catch (Exception exc)
        {
            logger.LogError(exc, "OpenWeather setting {SettingName} could not be loaded", settingName);
            return fallback;
        }
    }
}
