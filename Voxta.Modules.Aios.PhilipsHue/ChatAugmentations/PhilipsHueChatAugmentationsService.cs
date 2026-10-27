using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Services;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Modules.Aios.PhilipsHue.Configuration;
using Voxta.Modules.Aios.PhilipsHue.Clients;
using Voxta.Modules.Aios.PhilipsHue.Tools;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.PhilipsHue.ChatAugmentations;

public class PhilipsHueChatAugmentationsService(
    ILoggerFactory loggerFactory
) : ServiceBase(loggerFactory.CreateLogger<PhilipsHueChatAugmentationsService>()), IToolsChatAugmentationsService
{
    private static readonly ConcurrentDictionary<Guid, byte> InventoryNotesSentBySessionId = new();

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
                CreatePhilipsHueChatAugmentationsServiceInstance
            ),
        ]);
    }

    private async Task<IChatAugmentationServiceInstanceBase?> CreatePhilipsHueChatAugmentationsServiceInstance(
        IChatSessionChatAugmentationApi session,
        CancellationToken cancellationToken)
    {
        if (session.IsAugmentationEnabled(VoxtaModule.ToolsAugmentationKey)
            && !session.IsAugmentationSuppressed(VoxtaModule.ToolsAugmentationKey)) return null;
        return await CreateInstance(session, false, cancellationToken);
    }

    private AugmentationPlanItem CreateToolsPlan()
    {
        var plan = VoxtaModule.Augmentations.Get(VoxtaModule.ToolsAugmentationKey).Plan(
            AugmentationActivation.OptIn, (session, token) => CreateInstance(session, true, token));
        return new AugmentationPlanItem
        {
            Key = plan.Key, Label = plan.Label, Description = plan.Description,
            Activation = plan.Activation, ActivateAsync = plan.ActivateAsync, IncludeInCatchAll = false,
            Tools = HueToolCatalog.Choices.Select(c => new AugmentationToolDeclaration(c.Name, VoxtaModule.ServiceName)).ToArray(),
        };
    }

    private async Task<IChatAugmentationServiceInstanceBase?> CreateInstance(
        IChatSessionChatAugmentationApi session, bool tools, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger<PhilipsHueChatAugmentationsServiceInstance>();
        logger.LogInformation("Chat session {SessionId} has been augmented with {Augmentation}", session.SessionId, tools ? VoxtaModule.ToolsAugmentationKey : VoxtaModule.AugmentationKey);
        
        var authPath = PhilipsHueAuthPath.GetUserAuthPath(ModuleConfiguration.GetRequired(ModuleConfigurationProvider.AuthPath), Auth.UserId);

        var config = new PhilipsHueChatAugmentationsSettings
        {
            Actions = tools ? [] : NormalizeActions(
                ModuleConfiguration.GetOptional<PhilipsHueActionSettings>(ModuleConfigurationProvider.Actions),
                ModuleConfiguration.GetOptional(ModuleConfigurationProvider.CharacterControlledLight)),
            Ip = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.BridgeIp),
            Username = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.BridgeUsername),
            CharacterControlledLight = ModuleConfiguration.GetOptional(ModuleConfigurationProvider.CharacterControlledLight),
            AuthPath = authPath,
            SendInventoryAtSessionStart = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.SendInventoryAtSessionStart)
        };
        
        var hueUserInteractionWrapper = new HueUserInteractionWrapper(session, updateLegacyFlags: !tools);
        
        var colorConverterService = new ColorConverterService(loggerFactory.CreateLogger<ColorConverterService>());
        var bridgeConnectionService = new HueBridgeConnectionService(loggerFactory.CreateLogger<HueBridgeConnectionService>(), hueUserInteractionWrapper, config.AuthPath);
        var dataService = new HueDataService(bridgeConnectionService, loggerFactory.CreateLogger<HueDataService>());
        var commandService = new HueCommandService(bridgeConnectionService, dataService, loggerFactory.CreateLogger<HueCommandService>());
        var entityMatchingService = new HueEntityMatchingService(dataService, loggerFactory.CreateLogger<HueEntityMatchingService>());

        var manager = new HueManager(
            bridgeConnectionService,
            dataService,
            commandService,
            entityMatchingService,
            colorConverterService,
            loggerFactory.CreateLogger<HueManager>());
        
        await manager.InitializeAsync(cancellationToken);

        if (tools)
        {
            var handler = new HueToolHandler(manager, config.CharacterControlledLight);
            return new ModuleToolsAugmentation(HueToolCatalog.Create(handler, HueToolsSettings.From(ModuleConfiguration), config.CharacterControlledLight, logger));
        }

        var instance = new PhilipsHueChatAugmentationsServiceInstance(session, manager, config, logger);
        await TrySendHueInventoryNoteOncePerSessionAsync(session, instance, config, cancellationToken);
        return instance;
    }

    private static PhilipsHueActionSettings[] NormalizeActions(PhilipsHueActionSettings[]? actions, string? characterControlledLight)
    {
        if (actions is not { Length: > 0 })
            actions = ModuleConfigurationProvider.DefaultActions;

        var defaultsByName = ModuleConfigurationProvider.DefaultActions.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        return actions
            .Where(action => !string.IsNullOrWhiteSpace(action.Name) && defaultsByName.ContainsKey(action.Name))
            .Select(action =>
            {
                var defaultAction = defaultsByName[action.Name];
                return new PhilipsHueActionSettings
                {
                    Name = defaultAction.Name,
                    ShortDescription = string.IsNullOrWhiteSpace(action.ShortDescription) ? defaultAction.ShortDescription : action.ShortDescription,
                    Description = string.IsNullOrWhiteSpace(action.Description) ? defaultAction.Description : action.Description,
                    MatchFilter = NormalizeMultilineSetting(action.MatchFilter, defaultAction.MatchFilter),
                    FlagsFilter = string.IsNullOrWhiteSpace(action.FlagsFilter) ? defaultAction.FlagsFilter : action.FlagsFilter,
                    Disabled = NormalizeDisabled(action, characterControlledLight),
                    CancelReply = action.CancelReply,
                };
            })
            .ToArray();
    }

    private static string? NormalizeDisabled(PhilipsHueActionSettings action, string? characterControlledLight)
    {
        if (!string.Equals(action.Name, "show_emotion", StringComparison.OrdinalIgnoreCase))
            return action.Disabled;

        return string.IsNullOrWhiteSpace(action.Disabled) && string.IsNullOrWhiteSpace(characterControlledLight)
            ? "true"
            : action.Disabled;
    }

    private static string? NormalizeMultilineSetting(string? value, string? fallback)
    {
        var values = (value ?? "")
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        return values.Length == 0 ? fallback : string.Join(Environment.NewLine, values);
    }

    public static bool ParseActionBoolean(string? value, bool fallback)
    {
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static Task TrySendHueInventoryNoteOncePerSessionAsync(
        IChatSessionChatAugmentationApi session,
        PhilipsHueChatAugmentationsServiceInstance instance,
        PhilipsHueChatAugmentationsSettings config,
        CancellationToken cancellationToken)
    {
        if (!config.SendInventoryAtSessionStart)
            return Task.CompletedTask;

        return InventoryNotesSentBySessionId.TryAdd(session.SessionId, 0)
            ? instance.SendHueInventoryNoteAsync(cancellationToken)
            : Task.CompletedTask;
    }
}
