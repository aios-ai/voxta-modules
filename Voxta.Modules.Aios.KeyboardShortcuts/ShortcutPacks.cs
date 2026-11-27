using System.Text.Json;

namespace Voxta.Modules.Aios.KeyboardShortcuts;

internal static class ShortcutPacks
{
    internal const string Format = "voxta.keyboard-shortcuts";
    internal const int Version = 1;

    private static readonly HashSet<string> RowFields = new(StringComparer.Ordinal)
    {
        "name", "group", "description", "keys", "foregroundApp", "enabled",
    };

    internal sealed record Pack(string Format, int Version, JsonElement[] Shortcuts);
    internal sealed record Merge(JsonElement[] Added, string[] Duplicates, string[] Conflicts);

    internal static JsonElement[] ReadSaved(string raw) => string.IsNullOrWhiteSpace(raw)
        ? [] : JsonSerializer.Deserialize<JsonElement[]>(raw) ?? throw new InvalidDataException("The saved shortcut list is invalid.");

    internal static Pack Export(IEnumerable<JsonElement> rows) => new(Format, Version, rows.Select(PortableRow).ToArray());

    internal static Merge Preview(ShortcutSettings settings, JsonElement pack)
    {
        if (pack.ValueKind != JsonValueKind.Object || !pack.TryGetProperty("format", out var format)
            || format.ValueKind != JsonValueKind.String || format.GetString() != Format
            || !pack.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var number) || number != Version
            || !pack.TryGetProperty("shortcuts", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Choose a Keyboard Shortcuts group export (format version 1).");

        var incoming = rows.EnumerateArray().Select(PortableRow).ToArray();
        var validation = ShortcutConfiguration.ReadRows(incoming);
        if (validation.Errors.Length > 0) throw new InvalidDataException(string.Join(" ", validation.Errors));

        var entries = incoming.Select(row => (Row: row, Id: ShortcutConfiguration.GetIdentity(row))).ToArray();
        if (entries.Any(entry => entry.Id == null))
            throw new InvalidDataException("Every imported shortcut needs a valid name and group, including disabled shortcuts.");

        var seen = new Dictionary<(string Name, string? Group), JsonElement>();
        var seenKinds = settings.Groups.ToDictionary(group => group.Id, _ => true, StringComparer.Ordinal);
        foreach (var row in settings.Rows())
        {
            if (ShortcutConfiguration.GetIdentity(row) is not { } id) continue;
            seen.TryAdd(id, PortableRow(row));
            seenKinds.TryAdd(id.Group ?? id.Name, id.Group != null);
        }

        var added = new List<JsonElement>();
        var duplicates = new List<string>();
        var conflicts = new List<string>();
        foreach (var (row, identity) in entries)
        {
            var id = identity!.Value;
            var tool = id.Group ?? id.Name;
            var label = id.Group == null ? id.Name : $"{id.Group} / {id.Name}";
            var grouped = id.Group != null;
            if (seenKinds.TryGetValue(tool, out var wasGrouped) && wasGrouped != grouped)
                conflicts.Add(label);
            else if (seen.TryGetValue(id, out var held))
                (JsonElement.DeepEquals(row, held) ? duplicates : conflicts).Add(label);
            else
            {
                added.Add(row);
                seen.Add(id, row);
                seenKinds[tool] = grouped;
            }
        }

        return new(added.ToArray(), duplicates.ToArray(), conflicts.ToArray());
    }

    private static JsonElement PortableRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Each shortcut must be an object.");
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in row.EnumerateObject())
        {
            if (!RowFields.Contains(field.Name)) continue;
            if (field.Value.ValueKind != JsonValueKind.String
                && !(field.Name == "enabled" && field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException($"The {field.Name} field has an invalid value.");
            if (!values.TryAdd(field.Name, field.Value)) throw new InvalidDataException($"The {field.Name} field is repeated.");
        }
        return JsonSerializer.SerializeToElement(values);
    }
}
