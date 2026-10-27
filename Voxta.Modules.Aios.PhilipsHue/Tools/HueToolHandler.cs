using System.Globalization;
using System.Text.Json;
using Voxta.Abstractions.Services.Agents;
using Voxta.Modules.Aios.PhilipsHue.Clients;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.PhilipsHue.Tools;

internal sealed class HueToolHandler(HueManager hue, string? characterTarget)
{
    // Serialize connection, inventory and mutations against the session's mutable clients.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record Target(Guid Id, string Type, string Name, Guid OwnerId);
    private IEnumerable<Target> Targets() =>
        hue.GetLights().Select(x => new Target(x.Id, "light", x.Metadata?.Name ?? "", x.Id))
        .Concat(hue.GetRooms().Select(x => new Target(
            x.Services?.FirstOrDefault(s => s.Rtype == "grouped_light")?.Rid
            ?? x.GroupedServices?.FirstOrDefault(s => s.Rtype == "grouped_light")?.Rid ?? Guid.Empty, "room", x.Metadata?.Name ?? "", x.Id)))
        .Concat(hue.GetZones().Select(x => new Target(x.Services?.FirstOrDefault(s => s.Rtype == "grouped_light")?.Rid ?? Guid.Empty, "zone", x.Metadata?.Name ?? "", x.Id)))
        .Concat(hue.GetGroups().Where(x => !hue.GetRooms().Any(r => r.Services?.Any(s => s.Rid == x.Id) == true || r.GroupedServices?.Any(s => s.Rid == x.Id) == true)
            && !hue.GetZones().Any(z => z.Services?.Any(s => s.Rid == x.Id) == true))
            .Select(x => new Target(x.Id, "group", x.Metadata?.Name ?? "", x.Id)))
        .Where(x => !string.IsNullOrWhiteSpace(x.Name));

    private Target Resolve(string name)
    {
        var matches = Targets().Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].Id == Guid.Empty)
            throw new ModuleToolInputException($"Hue target '{name}' is missing, unavailable or ambiguous. List inventory and ask for a unique target name.");
        return matches[0];
    }

    public async ValueTask<ChatToolCallResult> ExecuteAsync(string operation, JsonElement? args, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!hue.IsConnected) return Failure();
            if (operation == "show_hue_inventory")
            {
                var targets = Targets().ToArray();
                return new(JsonSerializer.Serialize(new
                {
                    Targets = targets.Select(x => new { x.Name, x.Type }),
                    Scenes = hue.GetScenes().Select(s => new { Name = s.Metadata?.Name,
                        Targets = targets.Where(t => t.OwnerId == s.Group?.Rid || t.Id == s.Group?.Rid).Select(t => t.Name) }),
                }));
            }
            var turnOn = true;
            if (operation == "toggle_lights")
            {
                var state = Text(args, "state");
                if (state is not ("on" or "off")) throw new ModuleToolInputException("state must be on or off.");
                turnOn = state == "on";
            }
            var name = operation == "show_emotion" ? characterTarget : Text(args, "target", operation != "toggle_lights");
            if (operation == "show_emotion" && string.IsNullOrWhiteSpace(name))
                throw new ModuleToolInputException("Configure a character-controlled Hue target before using emotion control.");
            if (string.IsNullOrWhiteSpace(name))
            {
                var all = await hue.ControlAllLightsAsync(turnOn, token);
                return all ? Accepted() : Failure();
            }
            var target = Resolve(name);
            string? color = null;
            double? brightness = null;
            string? scene = null;
            var id = target.Id;
            var type = target.Type;
            if (operation is "change_color" or "show_emotion")
            {
                var colorName = Text(args, "color")!;
                var pascal = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(colorName.ToLowerInvariant()).Replace(" ", "");
                color = hue.TranslateColorNameToHex(colorName) ?? hue.TranslateColorNameToHex(pascal);
                if (string.IsNullOrWhiteSpace(color)) throw new ModuleToolInputException("Unknown Hue color. Supply a supported named color.");
            }
            if (operation == "change_brightness")
            {
                if (args is not { } obj || !obj.TryGetProperty("brightness", out var value)
                    || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number is < 1 or > 100)
                    throw new ModuleToolInputException("brightness must be an integer from 1 to 100.");
                brightness = number;
            }
            if (operation == "activate_scene")
            {
                scene = Text(args, "scene");
                if (type == "light") throw new ModuleToolInputException("Scenes require a room, zone or group target.");
                var matches = hue.GetScenes().Where(s => string.Equals(s.Metadata?.Name, scene, StringComparison.OrdinalIgnoreCase)
                    && (s.Group?.Rid == target.OwnerId || s.Group?.Rid == target.Id)).ToArray();
                if (matches.Length != 1) throw new ModuleToolInputException("Scene is missing or ambiguous for that target. List inventory and select a scene for this room or zone.");
                id = matches[0].Id;
            }
            var success = await hue.SendHueCommandAsync(id, type, state: turnOn, color: color, brightness: brightness, scene: scene, cancellationToken: token);
            return success ? Accepted() : Failure();
        }
        finally { _gate.Release(); }
    }

    private ChatToolCallResult Accepted() => new(hue.LastUserVisibleError is { } warning
        ? "Hue accepted part of the request: " + warning + " Do not automatically retry device commands."
        : "Hue bridge accepted the requested command. Physical light state has not been independently verified.");
    private ChatToolCallResult Failure() => new(hue.LastUserVisibleError ?? "Hue is disconnected or the bridge rejected the request. Check the bridge and restart the chat to run startup connection and pairing again.", IsError: true);
    private static string? Text(JsonElement? args, string key, bool required = true)
    {
        if (args is { } obj && obj.TryGetProperty(key, out var value))
        {
            if (value.ValueKind != JsonValueKind.String) throw new ModuleToolInputException(key + " must be a string.");
            if (!string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!.Trim();
            throw new ModuleToolInputException(key + " must not be empty; omit target only for all lights.");
        }
        if (required) throw new ModuleToolInputException(key + " is required.");
        return null;
    }
}
