using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Services;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Modules.Aios.PhilipsHue.Configuration;
using Voxta.Modules.Aios.PhilipsHue.Clients;
using Voxta.Modules.Aios.PhilipsHue.Tools;

namespace Voxta.Modules.Aios.PhilipsHue.ChatAugmentations;

public class PhilipsHueChatAugmentationsService(
    ILoggerFactory loggerFactory
) : ServiceBase(loggerFactory.CreateLogger<PhilipsHueChatAugmentationsService>()), IToolsChatAugmentationsService
{
    public Task<AugmentationPlanItem[]> PlanAsync(IChatAugmentationPlanContext context, CancellationToken cancellationToken) =>
        Task.FromResult<AugmentationPlanItem[]>([CreateToolsPlan()]);

    private AugmentationPlanItem CreateToolsPlan()
    {
        var plan = VoxtaModule.Augmentations.Get(VoxtaModule.ToolsAugmentationKey).Plan(
            AugmentationActivation.OptIn, CreateInstance);
        return new AugmentationPlanItem
        {
            Key = plan.Key, Label = plan.Label, Description = plan.Description,
            Activation = plan.Activation, ActivateAsync = plan.ActivateAsync, IncludeInCatchAll = false,
            Tools = HueToolCatalog.Choices.Select(c => new AugmentationToolDeclaration(c.Name, VoxtaModule.ServiceName)).ToArray(),
        };
    }

    private async Task<IChatAugmentationServiceInstanceBase?> CreateInstance(
        IChatSessionChatAugmentationApi session, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger<HueToolsSession>();
        logger.LogInformation("Chat session {SessionId} has been augmented with {Augmentation}", session.SessionId, VoxtaModule.ToolsAugmentationKey);
        
        var authPath = PhilipsHueAuthPath.GetUserAuthPath(ModuleConfiguration.GetRequired(ModuleConfigurationProvider.AuthPath), Auth.UserId);

        var characterTarget = ModuleConfiguration.GetOptional(ModuleConfigurationProvider.CharacterControlledLight);
        var hueUserInteractionWrapper = new HueUserInteractionWrapper(session);

        var colorConverterService = new ColorConverterService(loggerFactory.CreateLogger<ColorConverterService>());
        var bridgeConnectionService = new HueBridgeConnectionService(loggerFactory.CreateLogger<HueBridgeConnectionService>(), hueUserInteractionWrapper, authPath);
        var dataService = new HueDataService(bridgeConnectionService, loggerFactory.CreateLogger<HueDataService>());
        var commandService = new HueCommandService(bridgeConnectionService, dataService, loggerFactory.CreateLogger<HueCommandService>());

        var manager = new HueManager(
            bridgeConnectionService,
            dataService,
            commandService,
            colorConverterService,
            loggerFactory.CreateLogger<HueManager>());
        
        await manager.InitializeAsync(cancellationToken);

        var handler = new HueToolHandler(manager, characterTarget);
        var instance = new HueToolsSession(HueToolCatalog.Create(handler, HueToolsSettings.From(ModuleConfiguration), characterTarget, logger), manager, session);
        await instance.PublishStatusAsync(cancellationToken);
        return instance;
    }
}
