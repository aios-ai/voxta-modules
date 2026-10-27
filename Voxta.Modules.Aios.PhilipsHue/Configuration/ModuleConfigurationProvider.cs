using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.Aios.PhilipsHue.Tools;

namespace Voxta.Modules.Aios.PhilipsHue.Configuration;

public class ModuleConfigurationProvider : ModuleConfigurationProviderBase, IModuleConfigurationProvider
{
    public static string[] FieldsRequiringReload => ["HueToolCards", "ToolChatStyles", CharacterControlledLight.Name];
    public static readonly FormTextField CharacterControlledLight = new()
    {
        Name = "CharacterControlledLight",
        Label = "Character Controlled Light",
        DefaultValue = "",
        Text = "Target light, zone or room name the character can control on it's own (optional)",
    };

   public Task<FormField[]> GetModuleConfigurationFieldsAsync(
        IAuthenticationContext auth,
        ISettingsSource settings,
        CancellationToken cancellationToken
    )
   {
       var fields = FormBuilder.Build(
           CharacterControlledLight,
           HueToolsSettings.ConfigurationField(settings),
           HueToolsSettings.Styles.AsField()
       );
        return Task.FromResult(fields);
    }
}
