using Voxta.Abstractions.Modules;
using Voxta.Abstractions.Registration;
using Voxta.Model.Shared;
using Voxta.Modules.Aios.PhilipsHue.ChatAugmentations;
using Voxta.Modules.Aios.PhilipsHue.Configuration;

namespace Voxta.Modules.Aios.PhilipsHue;

public class VoxtaModule : IVoxtaModule
{
    public const string ServiceName = "Aios.PhilipsHue";
    public const string AugmentationKey = "hue";

    public static readonly AugmentationKeyDescriptor[] Augmentations =
    [
        new(
            AugmentationKey,
            "Philips Hue",
            "Control Philips Hue lights, rooms, zones, scenes, color, and brightness from a chat."
        ),
    ];

    public void Configure(IVoxtaModuleBuilder builder)
    {
        builder.Register(new()
        {
            ServiceName = ServiceName,
            Label = "Philips Hue (by aios)",
            Version = ModuleInfo.Version,
            Category = "Augmentations",
            Flags = [ModuleFlag.Experimental],
            InstallableBy = ModuleInstallableBy.Anyone,
            Supports = new()
            {
                ChatAugmentations = new(ServiceDefinitionCategoryScore.Medium),
            },
            Pricing = ServiceDefinitionPricing.Free,
            Hosting = ServiceDefinitionHosting.Online,
            Notes = "Control Philips Hue lights, rooms, zones, scenes, color, and brightness from Voxta chats. Requires a Philips Hue Bridge on the local network.",
            HelpLink = "https://github.com/aios-ai/voxta-modules",
            Logo = ModuleLogo.EmbeddedResource(typeof(VoxtaModule), "Assets.PhilipsHue.png"),
            Augmentations = Augmentations,
            ModuleConfigurationProviderType = typeof(ModuleConfigurationProvider),
            ModuleConfigurationFieldsRequiringReload = ModuleConfigurationProvider.FieldsRequiringReload,
            ModuleTestingProviderType = typeof(ModuleTestingProvider),
        });

        builder.AddChatAugmentationsService<PhilipsHueChatAugmentationsService>(ServiceName);
    }
}
