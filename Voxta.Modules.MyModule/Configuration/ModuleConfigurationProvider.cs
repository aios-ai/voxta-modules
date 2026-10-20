using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Model.Shared.Forms;

namespace Voxta.Modules.MyModule.Configuration;

public class ModuleConfigurationProvider : ModuleConfigurationProviderBase, IModuleConfigurationProvider
{
    public static string[] FieldsRequiringReload => [];

    public Task<FormField[]> GetModuleConfigurationFieldsAsync(
        IAuthenticationContext auth,
        ISettingsSource settings,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult(FormBuilder.Build(
            FormTitleField.Create("My Module")
        ));
    }
}
