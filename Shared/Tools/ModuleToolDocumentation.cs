using System.Text;
using Voxta.Model.Shared;
using Voxta.Model.Shared.Forms;

namespace Voxta.Modules.Aios.Tools;

/// <summary>Read-only configuration help derived from the agent's argument definitions.</summary>
internal static class ModuleToolDocumentation
{
    public static FormDocumentationField Arguments(
        IEnumerable<(string Name, FunctionArgumentDefinition[] Arguments)> tools)
    {
        var text = new StringBuilder("### Tool arguments\n\nThese are the arguments the agent can provide. Required arguments must be supplied; optional arguments can be omitted. Accepted values, limits and defaults are described below. Arguments are fixed by the module.\n");
        foreach (var (name, arguments) in tools)
        {
            text.Append("\n#### ").Append(name).Append("\n\n");
            if (arguments.Length == 0)
            {
                text.Append("No arguments.\n");
                continue;
            }

            foreach (var argument in arguments)
            {
                text.Append("- **").Append(argument.Name).Append("** (")
                    .Append(argument.Type.ToString().ToLowerInvariant()).Append(", ")
                    .Append(argument.Required ? "required" : "optional").Append("): ")
                    .Append(argument.Description).Append('\n');
            }
        }

        return FormDocumentationField.Create(text.ToString());
    }
}
