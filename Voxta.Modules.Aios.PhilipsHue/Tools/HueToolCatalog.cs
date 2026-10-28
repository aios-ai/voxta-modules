using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Services.Agents;
using Voxta.Model.Shared;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.PhilipsHue.Tools;

internal static class HueToolCatalog
{
    private sealed record Definition(string Name, string Description, FunctionArgumentDefinition[] Arguments);
    private static FunctionArgumentDefinition Text(string name, string description, bool required = true) => new()
        { Name = name, Description = description, Type = FunctionArgumentType.String, Required = required };
    private static readonly Definition[] Definitions =
    [
        new("toggle_lights", "Set a Hue target on or off. Omit target only when all lights are intended; all lights excludes smart plugs.", [Text("state", "on or off."), Text("target", "Exact inventory name of a light, smart plug, room, zone or group.", false)]),
        new("change_color", "Change a Hue target's color and turn it on.", [Text("target", "Exact inventory target name."), Text("color", "Named color, such as LightYellow or Red.")]),
        new("show_emotion", "Express a character emotion through the configured character-controlled target. Runs during the tool loop.", [Text("color", "Named color appropriate for the emotion.")]),
        new("change_brightness", "Set Hue brightness and turn the target on.", [Text("target", "Exact inventory target name."), new() { Name = "brightness", Description = "Brightness from 1 to 100 percent.", Type = FunctionArgumentType.Integer, Required = true }]),
        new("activate_scene", "Activate a named scene in a room, zone or group.", [Text("target", "Exact inventory room, zone or group name."), Text("scene", "Exact scene name for that target.")]),
        new("show_hue_inventory", "List available Hue lights, smart plugs, rooms, zones, groups and scenes with their target. Ask the user to resolve ambiguous names before controlling devices.", []),
    ];
    public static readonly ChatToolChoice[] Choices = Definitions.Select(d => new ChatToolChoice("hue_" + d.Name, d.Description, d.Name != "show_emotion")).ToArray();
    public static string DescribeArguments(string name) =>
        ModuleToolDocumentation.Arguments(Definitions[Array.FindIndex(Choices, choice => choice.Name == name)].Arguments);

    public static ModuleToolCatalog Create(HueToolHandler handler, HueToolsSettings settings, string? characterTarget, ILogger logger) => new(
        Definitions.Select((d, i) => new ModuleTool(new ChatTool(Choices[i].Name,
            settings.Descriptions?.GetValueOrDefault(Choices[i].Name) ?? d.Description,
            ChatToolSchema.FromArguments(d.Arguments), VoxtaModule.ServiceName)
        {
            AugmentationKey = VoxtaModule.ToolsAugmentationKey,
            Enabled = settings.Selected.Contains(Choices[i].Name) && (d.Name != "show_emotion" || !string.IsNullOrWhiteSpace(characterTarget)),
            ChatStyles = settings.ChatStyles,
            Permission = d.Name == "show_hue_inventory" ? ToolPermissionClass.Read : ToolPermissionClass.Act,
            ExternalContent = true,
        }, (args, token) => handler.ExecuteAsync(d.Name, args, token))), logger);
}
