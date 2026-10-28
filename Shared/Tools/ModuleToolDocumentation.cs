using Voxta.Model.Shared;
using Voxta.Model.Shared.Forms;

namespace Voxta.Modules.Aios.Tools;

/// <summary>Configuration-only argument help; never read as agent settings.</summary>
internal static class ModuleToolDocumentation
{
    public static string Arguments(FunctionArgumentDefinition[] arguments) => arguments.Length == 0
        ? "No arguments."
        : string.Join("\n\n", arguments.Select(argument =>
            $"{argument.Name} ({argument.Type.ToString().ToLowerInvariant()}, {(argument.Required ? "required" : "optional")}): {argument.Description}"));

    public static FormMultilineField ArgumentsField() => new()
    {
        Name = "Arguments", Label = "Arguments", Rows = 8,
    };

    // The SDK has no ReadOnly property. Its native dependency rule disables the
    // reference field; advanced fields are hidden in the card's inner editor.
    public static FormBooleanField ArgumentsLock() => new()
    {
        Name = "ArgumentsReadOnly", Label = "Module-defined arguments", Advanced = true,
        DefaultValue = true, DisablesFieldsOnActive = ["Arguments"],
    };
}
