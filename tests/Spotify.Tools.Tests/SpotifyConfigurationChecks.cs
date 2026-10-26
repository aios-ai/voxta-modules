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
            }),
        }));
        Check(settings.Selected.Contains("spotify_skip_next") && !settings.Selected.Contains("spotify_skip_previous"), "Card switches do not take precedence");
        Check(settings.Descriptions!["spotify_skip_next"] == "Custom explanation", "Description was lost");
        var catalog = SpotifyToolCatalog.Create(null!, settings, new Dictionary<string, string>(), NullLogger.Instance);
        var description = catalog.GetTools().Single(t => t.Name == "spotify_skip_next").Description;
        Check(description != null && description.StartsWith("Custom explanation") && description.Contains("spotify_list_devices") && description.Contains("ask the user"), "Custom description lost recovery/selection guidance");
        var fields = await new ModuleConfigurationProvider().GetModuleConfigurationFieldsAsync(null!, empty, default);
        Check(fields.OfType<FormAuthorizeField>().Single().Name == "SpotifyAccount", "Missing account field");
        Check(fields.OfType<FormDocumentationField>().Any(f => f.Value.Contains(SpotifyRedirectUri.DesktopDefault) && f.Value.Contains(SpotifyRedirectUri.VoxtaDefault)), "Both callbacks must be explained");
        foreach (var name in new[] { "AuthorizationMode", "VoxtaRedirectUri", "RedirectUri", "TokenPath" })
            Check(fields.All(f => f.Name != name), "Obsolete authentication setting still displayed: " + name);
        Console.WriteLine("PASS tool cards, editable descriptions, defaults, saved settings, and single-flow authentication configuration");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
