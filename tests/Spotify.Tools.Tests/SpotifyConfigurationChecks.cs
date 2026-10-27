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
        Check(empty.GetRequired(ModuleConfigurationProvider.SpeechDuckingMode) == "fixed", "Existing ducking mode changed");
        Check(empty.GetRequired(ModuleConfigurationProvider.SpeechDuckingVolumePercent) == 60, "Existing ducking limit changed");
        Check(empty.GetRequired(ModuleConfigurationProvider.SpeechDuckingReductionPercent) == 25, "Relative ducking default incorrect");
        var defaults = SpotifyToolsSettings.From(empty);
        Check(defaults.Selected.Count == 17, "New configurations must enable all tools");
        Check(defaults.ChatStyles.ToHashSet().SetEquals([ChatStyles.Assistant, ChatStyles.Companion]), "Default styles");
        var cards = (FormArrayOfObjectsField)SpotifyToolsSettings.ConfigurationField(empty);
        Check(cards.AllowDisable && !cards.AllowRename && !cards.AllowAddRemove, "Fixed tool card UI");
        Check(JsonSerializer.Deserialize<SpotifyToolSettings[]>(cards.DefaultValue!)!.Length == 17, "Missing cards");
        var old = new StaticSettingsSource(new() { ["Tools"] = "spotify_skip_next", ["ToolChatStyles"] = "Assistant" });
        var migrated = SpotifyToolsSettings.From(old);
        Check(migrated.Selected.SetEquals(["spotify_skip_next"]), "Legacy selection changed");
        Check(migrated.ChatStyles.SequenceEqual([ChatStyles.Assistant]), "Saved styles changed");
        var none = SpotifyToolsSettings.From(new StaticSettingsSource(new() { ["Tools"] = "" }));
        Check(none.Selected.Count == 17, "Empty beta.1 defaults should adopt enabled defaults");
        var disabledCards = JsonSerializer.Serialize(SpotifyToolCatalog.Choices.Select(c => new SpotifyToolSettings { Name = c.Name, Disabled = "true" }));
        Check(SpotifyToolsSettings.From(new StaticSettingsSource(new() { ["SpotifyTools"] = disabledCards })).Selected.Count == 0, "Explicit card disabling was not preserved");
        var settings = SpotifyToolsSettings.From(new StaticSettingsSource(new()
        {
            ["Tools"] = "",
            ["SpotifyTools"] = JsonSerializer.Serialize(new[]
            {
                new SpotifyToolSettings { Name = "spotify_skip_next", Description = "Custom explanation", Disabled = "false" },
                new SpotifyToolSettings { Name = "spotify_skip_previous", Disabled = "true" },
                new SpotifyToolSettings { Name = "spotify_play_music", Description = "Search Spotify and play the requested music or podcast.", Disabled = "false" },
            }),
        }));
        Check(settings.Selected.Contains("spotify_skip_next") && !settings.Selected.Contains("spotify_skip_previous"), "Card switches do not take precedence");
        Check(settings.Descriptions!["spotify_skip_next"] == "Custom explanation", "Description was lost");
        Check(settings.Selected.Contains("spotify_search"), "New search tool missing from saved card configuration");
        var catalog = SpotifyToolCatalog.Create(null!, settings, new Dictionary<string, string>(), NullLogger.Instance);
        var description = catalog.GetTools().Single(t => t.Name == "spotify_skip_next").Description;
        Check(description != null && description.StartsWith("Custom explanation") && description.Contains("start playback manually") && description.Contains("Wait for the user to confirm playback has started"), "Custom description lost manual playback prerequisite");
        var transfer = catalog.GetTools().Single(t => t.Name == "spotify_transfer_to_device");
        Check(catalog.GetTools().Single(t => t.Name == "spotify_play_music").Description!.Contains("exact readable Selection value from spotify_search"), "Saved description hid updated selection contract");
        Check(transfer.Description!.Contains("human-readable device name") && transfer.Description.Contains("user-requested destination changes"), "Name-only transfer guidance missing");
        var fields = await new ModuleConfigurationProvider().GetModuleConfigurationFieldsAsync(null!, empty, default);
        Check(fields.OfType<FormAuthorizeField>().Single().Name == "SpotifyAccount", "Missing account field");
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
        Check(saved.Length == 17 && saved.All(t => t.Name != "spotify_connect"), "Removed connect card retained");
        Check(saved.Single(t => t.Name == "spotify_skip_next").Disabled == "true", "Existing tool preference lost");
        Console.WriteLine("PASS tool cards, editable descriptions, defaults, saved settings, and single-flow authentication configuration");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
