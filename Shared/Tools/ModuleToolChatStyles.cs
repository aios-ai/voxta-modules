using Voxta.Model.Shared;
using Voxta.Model.Shared.Forms;

namespace Voxta.Modules.Aios.Tools;

internal static class ModuleToolChatStyles
{
    public const string FieldName = "ToolChatStyles";

    // Own the form definition: host builds may remove SDK static form fields.
    public static FormMultipleEnumField<ChatStyles> Create(string moduleName) => new()
    {
        Name = FieldName,
        Label = "Offer Tools In",
        Choices = Enum.GetValues<ChatStyles>().Select(style =>
            new FormMultipleEnumField<ChatStyles>.Choice { Label = style.ToString(), Value = style }).ToArray(),
        StartValues = [ChatStyles.Assistant, ChatStyles.Companion],
        Text = $"Offer {moduleName} tools in these chat styles. Defaults to Assistant and Companion.",
    };
}
