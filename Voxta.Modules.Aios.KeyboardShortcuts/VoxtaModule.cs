using Microsoft.Extensions.DependencyInjection;
using Voxta.Abstractions.Modules;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Utils;
using Voxta.Model.Shared;

namespace Voxta.Modules.Aios.KeyboardShortcuts;

public sealed class VoxtaModule : IVoxtaModule
{
    internal const string ServiceName = "Aios.KeyboardShortcuts";
    internal const string AugmentationKey = "keyboard_shortcuts";
    internal const string Label = "Keyboard Shortcuts";

    internal static readonly AugmentationKeyDescriptor Augmentation = new(
        AugmentationKey, Label, "Trigger keyboard shortcuts configured for the local Windows desktop.")
    {
        Kind = AugmentationKind.Tools,
    };

    public void Configure(IVoxtaModuleBuilder builder)
    {
        if (!OperatingSystem.IsWindows() || builder.Options.DisableComputerSessionAccess) return;

        builder.Register(new()
        {
            ServiceName = ServiceName,
            Label = Label,
            Version = ModuleInfo.Version,
            Category = "Augmentations",
            Flags = [ModuleFlag.Single, ModuleFlag.Experimental],
            InstallableBy = ModuleInstallableBy.AdminsOnly,
            Supports = new() { ChatAugmentations = new(ServiceDefinitionCategoryScore.Medium) },
            Pricing = ServiceDefinitionPricing.Free,
            Hosting = ServiceDefinitionHosting.Builtin,
            Notes = "Runs user-configured keyboard shortcuts on the local Windows desktop.",
            Augmentations = [Augmentation],
            Logo = ModuleLogo.EmbeddedResource(typeof(VoxtaModule), "Assets.logo.png"),
            ModuleConfigurationProviderType = typeof(ShortcutConfiguration),
        });

        builder.AddChatAugmentationsService<ShortcutAugmentationsService>(ServiceName);
        builder.Services.AddSingleton<WindowsKeyboard>();
        builder.Contribute<IModuleHttpEndpoints, ShortcutPacksEndpoints>();
    }
}
