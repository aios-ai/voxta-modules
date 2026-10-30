using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Voxta.Abstractions.Registration;
using Voxta.Model.Shared;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.Aios.Spotify.Configuration;
using Voxta.Modules.Aios.Spotify.Tools;

internal static class SpotifyConfigurationChecks
{
    public static async Task RunAsync()
    {
        var empty = new StaticSettingsSource(new());
        Check(ModuleConfigurationProvider.FieldsRequiringReload.Contains("ToolChatStyles"), "Style changes must reload the module");
        Check(empty.GetRequired(ModuleConfigurationProvider.SpeechDuckingMode) == "fixed", "Existing ducking mode changed");
        Check(empty.GetRequired(ModuleConfigurationProvider.SpeechDuckingVolumePercent) == 60, "Existing ducking limit changed");
        Check(empty.GetRequired(ModuleConfigurationProvider.SpeechDuckingReductionPercent) == 25, "Relative ducking default incorrect");
        var defaults = SpotifyToolsSettings.From(empty);
        Check(defaults.Selected.Count == 14 && !defaults.Selected.Contains("spotify_list_devices") && !defaults.Selected.Contains("spotify_transfer_to_device"), "Device tools must default off");
        Check(defaults.ChatStyles.ToHashSet().SetEquals([ChatStyles.Assistant, ChatStyles.Companion]), "Default styles");
        var cards = (FormArrayOfObjectsField)SpotifyToolsSettings.ConfigurationField(empty);
        Check(cards.AllowDisable && !cards.AllowRename && !cards.AllowAddRemove, "Fixed tool card UI");
        Check(JsonSerializer.Deserialize<SpotifyToolSettings[]>(cards.DefaultValue!)!.Length == 16, "Missing cards");
        var old = new StaticSettingsSource(new() { ["Tools"] = "spotify_skip_next", ["ToolChatStyles"] = "Assistant" });
        var migrated = SpotifyToolsSettings.From(old);
        Check(migrated.Selected.Count == 0, "A single enabled legacy direction must not enable both directions");
        Check(migrated.ChatStyles.SequenceEqual([ChatStyles.Assistant]), "Saved styles changed");
        var bothLegacy = SpotifyToolsSettings.From(new StaticSettingsSource(new() { ["Tools"] = "spotify_skip_next\nspotify_skip_previous" }));
        Check(bothLegacy.Selected.SetEquals(["spotify_skip"]), "Both enabled legacy directions did not migrate");
        foreach (var disabledNext in new[] { false, true })
        foreach (var disabledPrevious in new[] { false, true })
        {
            var legacyCards = JsonSerializer.Serialize(new[]
            {
                new SpotifyToolSettings { Name = "spotify_skip_next", Disabled = disabledNext.ToString(), Description = "Next only" },
                new SpotifyToolSettings { Name = "spotify_skip_previous", Disabled = disabledPrevious.ToString(), Description = "Previous only" },
            });
            var migratedSkip = SpotifyToolsSettings.From(new StaticSettingsSource(new() { ["SpotifyTools"] = legacyCards }));
            Check(migratedSkip.Selected.Contains("spotify_skip") == (!disabledNext && !disabledPrevious), "Skip migration overrode a disabled direction");
            Check(!migratedSkip.Descriptions!.ContainsKey("spotify_skip"), "Directional description copied to unified tool");
        }
        var none = SpotifyToolsSettings.From(new StaticSettingsSource(new() { ["Tools"] = "" }));
        Check(none.Selected.SetEquals(defaults.Selected), "Empty legacy defaults should adopt current defaults");
        var disabledCards = JsonSerializer.Serialize(SpotifyToolCatalog.Choices.Select(c => new SpotifyToolSettings { Name = c.Name, Disabled = "true" }));
        Check(SpotifyToolsSettings.From(new StaticSettingsSource(new() { ["SpotifyTools"] = disabledCards })).Selected.Count == 0, "Explicit card disabling was not preserved");
        var settings = SpotifyToolsSettings.From(new StaticSettingsSource(new()
        {
            ["Tools"] = "",
            ["SpotifyTools"] = JsonSerializer.Serialize(new[]
            {
                new SpotifyToolSettings { Name = "spotify_skip", Description = "Custom explanation", Disabled = "false" },
                new SpotifyToolSettings { Name = "spotify_skip_previous", Disabled = "true" },
                new SpotifyToolSettings { Name = "spotify_play_music", Description = "Search Spotify and play the requested music or podcast.", Disabled = "false" },
            }),
        }));
        Check(settings.Selected.Contains("spotify_skip") && !settings.Selected.Contains("spotify_skip_previous"), "Card switches do not take precedence");
        Check(settings.Descriptions!["spotify_skip"] == "Custom explanation", "Description was lost");
        Check(settings.Selected.Contains("spotify_search"), "New search tool missing from saved card configuration");
        var catalog = SpotifyToolCatalog.Create(null!, settings, new Dictionary<string, string>(), NullLogger.Instance);

        // Each card must show only its own schema, including when upgrading saved cards.
        var argumentField = (FormArrayOfObjectsField)SpotifyToolsSettings.ConfigurationField(new StaticSettingsSource(new()));
        var argumentCards = JsonSerializer.Deserialize<SpotifyToolSettings[]>(argumentField.DefaultValue!)!;
        Check(argumentField.FieldTemplate.OfType<FormMultilineField>().Any(f => f.Name == "Arguments"), "Missing Arguments inside tool card");
        var argumentLock = argumentField.FieldTemplate.OfType<FormBooleanField>().Single(f => f.Name == "ArgumentsReadOnly");
        Check(argumentLock.Advanced && argumentLock.DefaultValue == true && argumentLock.DisablesFieldsOnActive!.Contains("Arguments"), "Argument reference must be read-only");
        var savedArgumentCards = new StaticSettingsSource(new()
        {
            [SpotifyToolsSettings.CardsFieldName] = JsonSerializer.Serialize(new[]
            {
                new SpotifyToolSettings { Name = "spotify_skip", Description = "Saved card description", Disabled = "true", Arguments = "Not agent instructions" },
            }),
        });
        var savedArgumentSettings = SpotifyToolsSettings.From(savedArgumentCards);
        Check(!savedArgumentSettings.Selected.Contains("spotify_skip") && savedArgumentSettings.Descriptions!["spotify_skip"] == "Saved card description", "New card settings lost");
        var refreshedArgumentField = (FormArrayOfObjectsField)SpotifyToolsSettings.ConfigurationField(savedArgumentCards);
        var refreshedCard = JsonSerializer.Deserialize<SpotifyToolSettings[]>(refreshedArgumentField.DefaultValue!)!.Single(c => c.Name == "spotify_skip");
        Check(refreshedCard.Arguments == SpotifyToolCatalog.DescribeArguments("spotify_skip"), "Saved argument text must not configure the agent");
        foreach (var tool in catalog.GetTools())
        {
            var argumentHelp = argumentCards.Single(c => c.Name == tool.Name).Arguments!;
            var required = tool.InputSchema.TryGetProperty("required", out var requiredNames)
                ? requiredNames.EnumerateArray().Select(n => n.GetString()).ToHashSet() : [];
            var properties = tool.InputSchema.GetProperty("properties").EnumerateObject().ToArray();
            if (properties.Length == 0) Check(argumentHelp == "No arguments.", "No-argument tools must be explicit");
            foreach (var property in properties)
            {
                var expected = property.Name + " (" + property.Value.GetProperty("type").GetString()
                    + ", " + (required.Contains(property.Name) ? "required" : "optional") + "): "
                    + property.Value.GetProperty("description").GetString();
                Check(argumentHelp.Contains(expected), "Incomplete argument help for " + tool.Name + "." + property.Name);
            }
            Check(argumentHelp == SpotifyToolCatalog.DescribeArguments(tool.Name), "Wrong tool's arguments displayed");
        }
        var seekSchema = catalog.GetTools().Single(t => t.Name == "spotify_seek_playback").InputSchema;
        Check(seekSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(["target", "value"]), "UI argument text must not replace structured agent arguments");
        Check(seekSchema.GetProperty("required").EnumerateArray().Select(p => p.GetString()).SequenceEqual(["target"]), "Seek argument requirements changed");
        var previousCards = new StaticSettingsSource(new()
        {
            ["SpotifyToolsWithArguments"] = "[{\"Name\":\"spotify_repeat_mode\",\"Description\":\"Saved repeat description\",\"Disabled\":\"true\",\"Arguments\":\"mode (string, required): track, context, or off.\"}]",
        });
        var upgradedField = (FormArrayOfObjectsField)SpotifyToolsSettings.ConfigurationField(previousCards);
        var upgradedRepeat = JsonSerializer.Deserialize<SpotifyToolSettings[]>(upgradedField.DefaultValue!)!.Single(c => c.Name == "spotify_repeat_mode");
        Check(upgradedRepeat.Description == "Saved repeat description" && upgradedRepeat.Disabled == "true", "Argument reference refresh lost saved settings");
        Check(upgradedRepeat.Arguments!.Contains("current album or playlist"), "Saved argument reference was not refreshed");
        var description = catalog.GetTools().Single(t => t.Name == "spotify_skip").Description;
        Check(description == "Custom explanation", "Shared policies must not be duplicated into custom descriptions");
        var transfer = catalog.GetTools().Single(t => t.Name == "spotify_transfer_to_device");
        var guidance = SpotifyToolGuidance.Build(settings.Selected);
        Check(guidance.Contains("exact Selection returned by spotify_search") && guidance.Contains("Never automatically retry") && !guidance.Contains("device discovery"), "Shared guidance missing or includes disabled device tools");
        Check(transfer.InputSchema.GetProperty("properties").GetProperty("device").GetProperty("description").GetString()!.Contains("Unique device name"), "Name-only transfer schema missing");
        Check(!settings.Selected.Contains("spotify_list_devices") && !transfer.Enabled, "Missing device cards should default off");
        foreach (var flag in new[] { "true", "\"true\"" })
        {
            var raw = "[{\"name\":\"spotify_list_devices\",\"disabled\":" + flag + "},{\"name\":\"spotify_transfer_to_device\",\"disabled\":" + flag + "}]";
            var parsed = SpotifyToolsSettings.From(new StaticSettingsSource(new() { ["SpotifyTools"] = raw }));
            Check(!parsed.Selected.Contains("spotify_list_devices") && !parsed.Selected.Contains("spotify_transfer_to_device"), "Lowercase/boolean disabled switches ignored");
        }
        var enabledDevices = SpotifyToolsSettings.From(new StaticSettingsSource(new() { ["SpotifyTools"] = "[{\"Name\":\"spotify_list_devices\",\"Disabled\":\"false\"},{\"Name\":\"spotify_transfer_to_device\",\"Disabled\":\"false\"}]" }));
        Check(enabledDevices.Selected.Count == 16, "Explicit device opt-in lost");
        Check(SpotifyToolGuidance.Build(new HashSet<string>()).Length == 0, "Guidance for no tools");
        Check(SpotifyToolGuidance.Build(new HashSet<string> { "spotify_search" }).Length == 0, "Mutation guidance for search-only selection");
        var playOnly = SpotifyToolGuidance.Build(new HashSet<string> { "spotify_play_music" });
        Check(playOnly.Contains("Search is disabled") && !playOnly.Contains("spotify_queue_track") && !playOnly.Contains("device discovery"), "Guidance references disabled capabilities");
        var listOnly = SpotifyToolGuidance.Build(new HashSet<string> { "spotify_list_devices" });
        Check(listOnly.Contains("device discovery only") && !listOnly.Contains("transfer"), "Discovery-only guidance offers disabled transfer");
        var fields = await new ModuleConfigurationProvider(null!, new SpotifyOAuthFlows()).GetModuleConfigurationFieldsAsync(null!, empty, default);
        Check(fields.OfType<FormAuthorizeField>().Single().Name == "SpotifyAccount", "Missing account field");
        Check(fields.OfType<FormInvokeActionField>().Single().Name == ModuleConfigurationProvider.Reauthenticate.Name,
            "Missing native Spotify authorization recovery button");
        Check(fields.OfType<FormDocumentationField>().Any(f => f.Value.Contains(SpotifyRedirectUri.DesktopDefault) && f.Value.Contains(SpotifyRedirectUri.VoxtaDefault)), "Both callbacks must be explained");
        foreach (var name in new[] { "AuthorizationMode", "VoxtaRedirectUri", "RedirectUri", "TokenPath", "MatchFilterWakeWord", "Actions" })
            Check(fields.All(f => f.Name != name), "Obsolete authentication setting still displayed: " + name);
        var obsolete = new StaticSettingsSource(new()
        {
            ["SpotifyTools"] = JsonSerializer.Serialize(new[]
            {
                new SpotifyToolSettings { Name = "spotify_connect", Disabled = "false" },
                new SpotifyToolSettings { Name = "spotify_skip_next", Disabled = "true" },
            }),
            ["Actions"] = "old-unused-actions",
            ["MatchFilterWakeWord"] = "Spotify",
            ["TokenPath"] = "invalid-old-path",
        });
        var migratedCards = (FormArrayOfObjectsField)SpotifyToolsSettings.ConfigurationField(obsolete);
        var saved = JsonSerializer.Deserialize<SpotifyToolSettings[]>(migratedCards.DefaultValue!)!;
        Check(saved.All(c => c.Arguments == SpotifyToolCatalog.DescribeArguments(c.Name)), "Migrated arguments missing");
        Check(saved.Length == 16 && saved.All(t => t.Name != "spotify_connect"), "Removed connect card retained");
        Check(saved.Single(t => t.Name == "spotify_skip").Disabled == "true", "Existing tool preference lost");
        Console.WriteLine("PASS tool cards, editable descriptions, defaults, saved settings, and single-flow authentication configuration");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
