using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Authorization;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Repositories;
using Voxta.Abstractions.Security;
using Voxta.Model.ApiMessages.Requests;
using Voxta.Model.ApiMessages.Responses;
using Voxta.Model.Shared;
using Voxta.Model.Shared.Forms;

namespace Voxta.Modules.Aios.KeyboardShortcuts;

internal sealed record Shortcut(string Name, string? Group, string Description, string? ForegroundApp, ushort[] Keys)
{
    public string ToolName => Group ?? Name;

    public bool HasSameBinding(Shortcut other) => Name == other.Name && Group == other.Group
        && string.Equals(ForegroundApp, other.ForegroundApp, StringComparison.OrdinalIgnoreCase)
        && Keys.AsSpan().SequenceEqual(other.Keys);
}

public sealed class ShortcutConfiguration(IUnitOfWorkFactory unitOfWorkFactory) : ModuleConfigurationProviderBase, IModuleConfigurationProvider
{
    private const string NameField = "name";
    private const string GroupField = "group";
    private const string DescriptionField = "description";
    private const string KeysField = "keys";
    private const string ForegroundAppField = "foregroundApp";
    private const string NamePattern = "^[A-Za-z][A-Za-z0-9_ -]{0,63}$";
    private const string AppPattern = "^[^<>:\"/\\\\|?*\\x00-\\x1F]+[.][eE][xX][eE]$";

    internal const string FieldName = "Shortcuts";
    private const string UpgradeAction = "OrganizeShortcuts";

    private static FormArrayOfObjectsField CreateShortcutList(string name, string label, string? text = null) => new()
    {
        Name = name,
        Label = label,
        Text = text,
        AdminOnly = true,
        ButtonLabel = "Add shortcut",
        FieldTemplate =
        [
            new FormTextField
            {
                Name = NameField, Label = "Shortcut name", Required = true, Placeholder = "take_screenshot",
                ValidationRegex = NamePattern, Help = "Start with a letter. Names must be unique within a group or among standalone shortcuts. For example, Take Screenshot becomes take_screenshot.",
            },
            new FormTextField
            {
                    Name = DescriptionField, Label = "What it does", Required = true,
                    Placeholder = "Take a screenshot in the current game",
            },
            new FormTextField
            {
                Name = KeysField, Label = "Key combination", Required = true, Placeholder = "Ctrl+Shift+F10",
                ValidationRegex = WindowsShortcutKeys.Pattern,
                Help = WindowsShortcutKeys.Guide,
            },
            new FormTextField
            {
                Name = ForegroundAppField, Label = "Foreground app (optional)", Placeholder = "application.exe",
                ValidationRegex = AppPattern,
                Help = "Enter the executable name from Task Manager to require that app to have focus. Leave blank for global shortcuts.",
            },
        ],
    };

    private static readonly FormArrayOfObjectsField Groups = new()
    {
        Name = ShortcutSettings.GroupsField,
        Label = "Shortcut groups",
        Text = "Select a group to edit its shortcuts. Each enabled group appears as one chat tool.",
        Help = "Enable the Keyboard Shortcuts module in the character settings. Reopen an active chat after a change.",
        AdminOnly = true,
        ButtonLabel = "Add group",
        UniqueFields = [NameField],
        FieldTemplate =
        [
            new FormTextField
            {
                Name = NameField, Label = "Group name", Required = true, Placeholder = "Windows", ValidationRegex = NamePattern,
                Text = "Choose a name that differs from every other group and standalone shortcut.",
            },
            CreateShortcutList(ShortcutSettings.RowsField, "Group shortcuts"),
        ],
    };

    private static readonly FormArrayOfObjectsField Standalone = CreateShortcutList(FieldName, "Standalone shortcuts",
        "Each shortcut here appears as its own chat tool.");

    private static readonly FormInvokeActionField Organize = new()
    {
        Name = UpgradeAction,
        Label = "Use grouped settings",
        ButtonText = "Organize existing shortcuts",
        AdminOnly = true,
    };

    public Task<FormField[]> GetModuleConfigurationFieldsAsync(
        IAuthenticationContext auth, ISettingsSource settings, CancellationToken cancellationToken)
    {
        var shortcuts = settings.GetRawValue(FieldName);
        var groups = settings.GetRawValue(ShortcutSettings.GroupsField);
        var result = ValidateSettings(shortcuts, groups);
        var issues = result.Errors.Length == 0 ? null : FormDocumentationField.Create(
            "**Unavailable shortcuts**\n\n" + string.Join("\n", result.Errors.Select(error => $"- {error}")));
        if (issues != null) issues.AdminOnly = true;
        var packs = FormDocumentationField.Create(
            $"[Shortcut Groups]({ShortcutPacksEndpoints.BasePath}/) shows saved shortcuts by group and lets you export or merge a group. Save your edits here before opening it. Reopen these settings after an import to see the new shortcuts.");
        packs.AdminOnly = true;
        if (string.IsNullOrWhiteSpace(groups) && NeedsOrganization(shortcuts))
            return Task.FromResult(FormBuilder.Build(Organize, packs, issues,
                new FormHiddenField { Name = FieldName, Label = "Saved shortcuts", AdminOnly = true },
                new FormHiddenField { Name = ShortcutSettings.GroupsField, Label = "Saved groups", AdminOnly = true }));

        return Task.FromResult(FormBuilder.Build(Groups, Standalone, packs, issues));
    }

    public override Task UpdateModuleConfigurationAsync(ISettingsSource settings)
    {
        var result = ValidateSettings(settings.GetRawValue(FieldName), settings.GetRawValue(ShortcutSettings.GroupsField));
        if (result.Errors.Length > 0)
        {
            throw new UserSolvableException(string.Join(" ", result.Errors),
                "Correct the listed shortcuts or disable those rows, then save again.", "Invalid keyboard shortcut configuration");
        }

        return Task.CompletedTask;
    }

    public override async Task<FormInvokeActionResponse> InvokeAction(IAuthenticationContext auth, Guid moduleId,
        StaticSettingsSource settings, FormInvokeActionRequest request, CancellationToken cancellationToken)
    {
        if (!auth.IsAdmin()) throw new UnauthorizedAccessException();
        if (request.FieldName != UpgradeAction) throw new NotSupportedException();

        await ShortcutSettings.WriteLock.WaitAsync(cancellationToken);
        try
        {
            return await OrganizeSavedShortcutsAsync(auth.UserId, moduleId, cancellationToken);
        }
        finally
        {
            ShortcutSettings.WriteLock.Release();
        }
    }

    private async Task<FormInvokeActionResponse> OrganizeSavedShortcutsAsync(Guid userId, Guid moduleId,
        CancellationToken cancellationToken)
    {
        await using var work = unitOfWorkFactory.CreateUnitOfWork(userId);
        var module = await work.Modules.GetByIdAsync(moduleId, cancellationToken);
        if (module?.ServiceName != VoxtaModule.ServiceName) throw new InvalidOperationException("Keyboard Shortcuts was not found.");

        if (string.IsNullOrWhiteSpace(module.Configuration.GetValueOrDefault(ShortcutSettings.GroupsField)))
        {
            try
            {
                ShortcutSettings.Read(module.Configuration.GetValueOrDefault(FieldName, ""), "").SaveTo(module.Configuration);
                await work.Modules.UpdateAsync(module);
                await work.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                return new() { Text = "Grouping could not be applied to the saved shortcuts. The stored list was retained." };
            }
        }

        return new() { Text = "Your shortcuts are organized. Reopen Keyboard Shortcuts settings to edit groups and standalone shortcuts." };
    }

    private static bool NeedsOrganization(string raw)
    {
        try
        {
            return ShortcutPacks.ReadSaved(raw).Any(row => row.ValueKind == JsonValueKind.Object
                && row.TryGetProperty(GroupField, out var group)
                && (group.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(group.GetString())));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return false;
        }
    }

    internal static Shortcut[] Read(string raw, string rawGroups, ILogger logger)
    {
        var result = ValidateSettings(raw, rawGroups, includeDisabledGroups: false);
        if (result.Errors.Length > 0)
            logger.LogDebug("Keyboard Shortcuts skipped unavailable entries: {Errors}", string.Join(" ", result.Errors));
        return result.Shortcuts;
    }

    internal sealed record Configuration(Shortcut[] Shortcuts, string[] Errors);

    private static Configuration ValidateSettings(string raw, string rawGroups, bool includeDisabledGroups = true)
    {
        if (string.IsNullOrWhiteSpace(rawGroups)) return ReadConfiguration(raw);
        try
        {
            var settings = ShortcutSettings.Read(raw, rawGroups);
            return ReadRows(settings.Rows(includeDisabledGroups));
        }
        catch (InvalidDataException exception)
        {
            return new([], [exception.Message]);
        }
        catch (JsonException)
        {
            return new([], ["The shortcut groups or standalone shortcuts are not valid JSON."]);
        }
    }

    internal static Configuration ReadConfiguration(string raw)
    {
        try
        {
            return Read(raw);
        }
        catch (JsonException)
        {
            return new([], ["The shortcut list is not valid JSON."]);
        }
    }

    private static Configuration Read(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new([], []);

        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return new([], ["Shortcuts must be a list."]);
        return ReadRows(document.RootElement.EnumerateArray());
    }

    internal static Configuration ReadRows(IEnumerable<JsonElement> rows)
    {
        // A duplicate shortcut within one group is ambiguous. The same name in two groups is fine.
        var errors = new List<string>();
        var shortcuts = rows.Select((row, index) => ParseRow(row, index + 1, errors))
            .OfType<Shortcut>()
            .ToArray();
        var available = new List<Shortcut>();
        foreach (var tool in shortcuts.GroupBy(shortcut => shortcut.ToolName, StringComparer.Ordinal))
        {
            if (tool.Any(shortcut => shortcut.Group == null) && tool.Any(shortcut => shortcut.Group != null))
            {
                errors.Add($"`{tool.Key}` is used as both a group and a standalone shortcut.");
                continue;
            }

            foreach (var choice in tool.GroupBy(shortcut => shortcut.Name, StringComparer.Ordinal))
            {
                if (choice.Count() == 1) available.Add(choice.Single());
                else errors.Add($"Shortcut `{choice.Key}` is listed more than once under tool `{tool.Key}`.");
            }
        }
        return new(available.ToArray(), errors.ToArray());
    }

    private static Shortcut? ParseRow(JsonElement row, int index, List<string> errors)
    {
        Shortcut? Invalid(string message)
        {
            errors.Add($"Row {index}: {message}");
            return null;
        }

        if (row.ValueKind != JsonValueKind.Object) return Invalid("Expected a shortcut entry.");
        if (row.TryGetProperty("enabled", out var enabled) && enabled.ValueKind != JsonValueKind.True
            && !(enabled.ValueKind == JsonValueKind.String && enabled.GetString() == "true"))
            return enabled.ValueKind == JsonValueKind.False || (enabled.ValueKind == JsonValueKind.String && enabled.GetString() == "false")
                ? null : Invalid("The enabled value must be true or false.");

        if (ReadText(row, NameField) is not { } name || !Regex.IsMatch(name, NamePattern))
            return Invalid("Enter a valid shortcut name.");
        if (ReadText(row, KeysField) is not { } keys || !WindowsShortcutKeys.TryParse(keys, out var chord))
            return Invalid("Enter a supported key combination. See the info icon next to Key combination.");
        if (!TryReadOptional(row, GroupField, NamePattern, out var group)) return Invalid("Enter a valid group name or leave it blank.");
        if (!TryReadOptional(row, ForegroundAppField, AppPattern, out var app)) return Invalid("Enter an executable name ending in .exe or leave it blank.");

        var description = ReadText(row, DescriptionField);
        return new Shortcut(ToolId(name), group == null ? null : ToolId(group),
            string.IsNullOrWhiteSpace(description) ? name.Replace('_', ' ') : description, app, chord);
    }

    internal static bool IsName(string? name) => name != null && Regex.IsMatch(name, NamePattern);

    internal static string ToolId(string name) => Regex.Replace(name, "[ -]+", "_").ToLowerInvariant();

    internal static (string Name, string? Group)? GetIdentity(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || ReadText(row, NameField) is not { } name
            || !Regex.IsMatch(name, NamePattern) || !TryReadOptional(row, GroupField, NamePattern, out var group)) return null;
        return (ToolId(name), group == null ? null : ToolId(group));
    }

    private static bool TryReadOptional(JsonElement row, string property, string pattern, out string? text)
    {
        text = null;
        if (!row.TryGetProperty(property, out var field)) return true;
        if (field.ValueKind != JsonValueKind.String) return false;
        var value = field.GetString()!.Trim();
        if (value.Length == 0) return true;
        text = value;
        return Regex.IsMatch(value, pattern);
    }

    internal static string? ReadText(JsonElement row, string property) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() : null;
}
