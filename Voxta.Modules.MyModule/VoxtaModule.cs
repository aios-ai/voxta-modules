using Voxta.Abstractions.Modules;
using Voxta.Abstractions.Registration;
using Voxta.Modules.MyModule.Configuration;

namespace Voxta.Modules.MyModule;

public class VoxtaModule : IVoxtaModule
{
    /// <summary>Unique across every module Voxta loads.</summary>
    public const string ServiceName = "MyModule";

    public const string AugmentationKey = "my_module";

    public void Configure(IVoxtaModuleBuilder builder)
    {
        builder.Register(new()
        {
            ServiceName = ServiceName,
            Label = "My Module",
            Version = ModuleInfo.Version,
            Experimental = true,
            Supports = new()
            {
                // Define your supported services
            },
            Pricing = ServiceDefinitionPricing.Free,
            Hosting = ServiceDefinitionHosting.Builtin,
            SupportsExplicitContent = true,
            Notes = "A starting point for your own module.",
            Augmentations = [],
            Logo = ModuleLogo.EmbeddedResource(typeof(VoxtaModule), "Assets.logo.svg"),
            ModuleConfigurationProviderType = typeof(ModuleConfigurationProvider),
            ModuleConfigurationFieldsRequiringReload = ModuleConfigurationProvider.FieldsRequiringReload,
        });

        // Register your service dependencies
    }
}
