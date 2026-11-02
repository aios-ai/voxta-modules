using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Services;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Modules.Translator.Configuration;
using Voxta.Modules.Translator.Translation;

namespace Voxta.Modules.Translator.ChatAugmentations;

public sealed class TranslatorAugmentationsService(
    ILogger<TranslatorAugmentationsService> logger,
    TranslatorEngineRegistry registry)
    : ServiceBase(logger), IChatAugmentationsService
{
    public Task<AugmentationPlanItem[]> PlanAsync(
        IChatAugmentationPlanContext context,
        CancellationToken cancellationToken)
    {
        var engine = registry.GetRequired(ModuleConfiguration.GetRequired(ModuleConfigurationProvider.Engine));
        var userLanguage = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.UserLanguage);
        var replyLanguage = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.ChatLanguage);

        return Task.FromResult<AugmentationPlanItem[]>(
        [
            VoxtaModule.Augmentations.Get(VoxtaModule.AugmentationKey).Plan(
                AugmentationActivation.OptIn,
                session => new TranslatorAugmentation(
                    session,
                    engine,
                    userLanguage,
                    replyLanguage,
                    logger)),
        ]);
    }
}
