using System.Text.Json;

namespace Voxta.Modules.KeyboardShortcuts;

internal sealed class ShortcutSettings
{
    internal const string GroupsField = "ShortcutGroups";
    internal const string RowsField = "shortcuts";
    private const string EnabledField = "enabled";
    private const string GroupField = "group";
    internal static readonly SemaphoreSlim WriteLock = new(1);

    internal sealed record Group(string Name, string Id, bool Enabled, List<JsonElement> Rows);

    public List<JsonElement> Standalone { get; } = [];
    public List<Group> Groups { get; } = [];

    public static ShortcutSettings Read(string shortcuts, string groups)
    {
        var settings = new ShortcutSettings();
        var rows = ShortcutPacks.ReadSaved(shortcuts);
        if (string.IsNullOrWhiteSpace(groups))
        {
            // Older builds stored every shortcut in one list.
            foreach (var row in rows)
            {
                var name = ReadGroup(row);
                var target = name == null ? settings.Standalone : settings.GetOrAddGroup(name).Rows;
                target.Add(row);
            }
        }
        else
        {
            settings.Standalone.AddRange(rows);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in ShortcutPacks.ReadSaved(groups))
            {
                var name = ShortcutConfiguration.ReadText(row, "name");
                if (!ShortcutConfiguration.IsName(name)) throw new InvalidDataException("Enter a valid name for each group.");
                var id = ShortcutConfiguration.ToolId(name!);
                if (!names.Add(id)) throw new InvalidDataException($"Group `{id}` appears more than once.");

                var entries = row.TryGetProperty(RowsField, out var children) ? children.ValueKind switch
                {
                    JsonValueKind.Array => children.EnumerateArray().ToArray(),
                    JsonValueKind.String => ShortcutPacks.ReadSaved(children.GetString() ?? ""),
                    _ => throw new InvalidDataException($"Group `{id}` must contain a list of shortcuts."),
                } : [];
                settings.Groups.Add(new Group(name!, id, IsGroupEnabled(row), [.. entries]));
            }
        }

        settings.ValidateNames();
        return settings;
    }

    public JsonElement[] Rows(bool includeDisabledGroups = true) =>
    [
        .. Standalone.Select(row => WithGroup(row, null)),
        .. Groups.Where(group => includeDisabledGroups || group.Enabled)
            .SelectMany(group => group.Rows.Select(row => WithGroup(row, group.Name))),
    ];

    public void Add(JsonElement row)
    {
        var group = ReadGroup(row);
        var target = group == null ? Standalone : GetOrAddGroup(group).Rows;
        target.Add(row);
    }

    public void SaveTo(Dictionary<string, string> configuration)
    {
        configuration[ShortcutConfiguration.FieldName] = JsonSerializer.Serialize(Standalone.Select(row => WithGroup(row, null)));
        configuration[GroupsField] = JsonSerializer.Serialize(Groups.Select(group => new
        {
            name = group.Name,
            enabled = group.Enabled ? "true" : "false",
            shortcuts = JsonSerializer.Serialize(group.Rows.Select(row => WithGroup(row, null))),
        }));
    }

    public static JsonElement WithGroup(JsonElement row, string? group)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Each shortcut must be an object.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in row.EnumerateObject())
        {
            if (field.Name != GroupField) fields[field.Name] = field.Value;
        }
        if (group != null) fields[GroupField] = JsonSerializer.SerializeToElement(group);
        return JsonSerializer.SerializeToElement(fields);
    }

    private Group GetOrAddGroup(string name)
    {
        var id = ShortcutConfiguration.ToolId(name);
        var group = Groups.Find(item => item.Id == id);
        if (group != null) return group;
        group = new(name, id, true, []);
        Groups.Add(group);
        return group;
    }

    private void ValidateNames()
    {
        var names = Groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var shortcut in Standalone)
        {
            var name = ShortcutConfiguration.ReadText(shortcut, "name");
            if (ShortcutConfiguration.IsName(name) && names.Contains(ShortcutConfiguration.ToolId(name!)))
                throw new InvalidDataException($"`{ShortcutConfiguration.ToolId(name!)}` is used as both a group and a standalone shortcut.");
        }
    }

    private static string? ReadGroup(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Each shortcut must be an object.");
        if (!row.TryGetProperty(GroupField, out var field)) return null;
        if (field.ValueKind != JsonValueKind.String) throw new InvalidDataException("Enter a valid group name or leave it blank.");
        var name = field.GetString()!.Trim();
        if (name.Length == 0) return null;
        return ShortcutConfiguration.IsName(name) ? name : throw new InvalidDataException("Enter a valid group name or leave it blank.");
    }

    private static bool IsGroupEnabled(JsonElement row)
    {
        if (!row.TryGetProperty(EnabledField, out var enabled)) return true;
        return enabled.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when enabled.GetString() == "true" => true,
            JsonValueKind.String when enabled.GetString() == "false" => false,
            _ => throw new InvalidDataException("A group must be enabled or disabled."),
        };
    }
}
