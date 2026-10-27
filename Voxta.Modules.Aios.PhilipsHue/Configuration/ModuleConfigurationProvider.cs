using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.Aios.PhilipsHue.Tools;

namespace Voxta.Modules.Aios.PhilipsHue.Configuration;

public class ModuleConfigurationProvider : ModuleConfigurationProviderBase, IModuleConfigurationProvider
{
    public static string[] FieldsRequiringReload => ["HueToolCards", "HueTools", "ToolChatStyles", CharacterControlledLight.Name, AuthPath.Name];
    private static readonly string DefaultAuthPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Voxta",
        "Aios.PhilipsHue",
        "Voxta.Modules.Aios.PhilipsHue.Auth.json");
    
    public static readonly FormTextField CharacterControlledLight = new()
    {
        Name = "CharacterControlledLight",
        Label = "Character Controlled Light",
        DefaultValue = "",
        Text = "Target light, zone or room name the character can control on it's own (optional)",
    };

    public static readonly FormTextField AuthPath = new()
    {
        Name = "AuthPath",
        Label = "Authentication Path",
        Required = true,
        Text = "The path to store the PhilipsHue authentication token.",
        DefaultValue = DefaultAuthPath,
        Advanced = true,
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
           HueToolsSettings.Styles.AsField(),
           new FormHiddenField { Name = AuthPath.Name }
       );
        return Task.FromResult(fields);
    }
}
