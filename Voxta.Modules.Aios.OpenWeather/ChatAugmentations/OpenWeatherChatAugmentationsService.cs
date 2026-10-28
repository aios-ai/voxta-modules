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
        ]);
    }

    private AugmentationPlanItem CreateToolsPlan()
    {
        var plan = VoxtaModule.Augmentations.Get(VoxtaModule.ToolsAugmentationKey).Plan(
            AugmentationActivation.OptIn, CreateInstance);
        return new AugmentationPlanItem
        {
            Key = plan.Key, Label = plan.Label, Description = plan.Description,
            Activation = plan.Activation, ActivateAsync = plan.ActivateAsync, IncludeInCatchAll = false,
            Tools = OpenWeatherToolCatalog.Choices.Select(choice => new AugmentationToolDeclaration(choice.Name, VoxtaModule.ServiceName)).ToArray(),
        };
    }

    private Task<IChatAugmentationServiceInstanceBase?> CreateInstance(
        IChatSessionChatAugmentationApi session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var logger = loggerFactory.CreateLogger<OpenWeatherToolHandler>();
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
        var config = new OpenWeatherChatAugmentationsSettings
        {
            MyLocation = GetSettingOrDefault(() => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.MyLocation), string.Empty, "MyLocation", logger),
            Units = GetSettingOrDefault(() => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.Units), "metric", "Units", logger),
            WeatherDetails = selectedWeather.ToArray(),
            PollutionDetails = selectedPollution.ToArray(),
            TileCachePath = tileCachePath,
        };
        var handler = new OpenWeatherToolHandler(client, config, session.MainCharacter.Culture,
            session.SendNoteAttachmentAsync, session.Abort);
        logger.LogInformation("Chat session {SessionId} has been augmented with {Augmentation}", session.SessionId, VoxtaModule.ToolsAugmentationKey);
        return Task.FromResult<IChatAugmentationServiceInstanceBase?>(new ModuleToolsAugmentation(
            OpenWeatherToolCatalog.Create(handler, OpenWeatherToolsSettings.From(ModuleConfiguration), logger)));
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
