using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Repositories;
using Voxta.Abstractions.Services;
using Voxta.Abstractions.Services.ChatAugmentations;

namespace Voxta.Modules.KeyboardShortcuts;

internal sealed class ShortcutAugmentationsService(
    WindowsKeyboard keyboard, IUnitOfWorkFactory unitOfWorkFactory, ILogger<ShortcutAugmentationsService> logger)
    : ServiceBase(logger), IToolsChatAugmentationsService
{
    public Task<AugmentationPlanItem[]> PlanAsync(IChatAugmentationPlanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Auth.Remote || context.Auth.Unattended) return Task.FromResult<AugmentationPlanItem[]>([]);

        var userId = context.Auth.UserId;
        var moduleId = InstanceSettings.ModuleId;
        var configured = InstanceSettings.ModuleConfiguration;
        var tools = ShortcutConfiguration.Read(configured.GetValueOrDefault(ShortcutConfiguration.FieldName, ""),
                configured.GetValueOrDefault(ShortcutSettings.GroupsField, ""), logger)
            .Select(shortcut => shortcut.ToolName)
            .Distinct(StringComparer.Ordinal)
            .Select(name => new AugmentationToolDeclaration(name, VoxtaModule.ServiceName))
            .ToArray();
        return Task.FromResult<AugmentationPlanItem[]>(
        [
            new()
            {
                Key = VoxtaModule.Augmentation.Key,
                Label = VoxtaModule.Augmentation.Label,
                Description = VoxtaModule.Augmentation.Description,
                Activation = AugmentationActivation.OptIn,
                IncludeInCatchAll = false,
                Tools = tools,
                ActivateAsync = (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult<IChatAugmentationServiceInstanceBase?>(
                        new ShortcutToolsSession(keyboard, unitOfWorkFactory, userId, moduleId, logger));
                },
            },
        ]);
    }
}
