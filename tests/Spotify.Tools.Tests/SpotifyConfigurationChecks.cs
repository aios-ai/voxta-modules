using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Utils;
using Voxta.Model.Shared;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.Aios.Spotify.Clients.Services;
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
        Check(catalog.GetTools().Single(t => t.Name == "spotify_skip_next").Description == "Custom explanation", "Description not offered to model");
        foreach (var value in new[]
        {
            "http://127.0.0.1:5384/extensions/spotify/oauth2/callback",
            "http://127.0.0.1:5385/extensions/spotify/oauth2/callback.",
            "http://127.0.0.1:5385/api/extensions/spotify/oauth2/callback",
            "http://localhost:5385/extensions/spotify/oauth2/callback",
            "http://example.com:5385/extensions/spotify/oauth2/callback",
            "http://127.0.0.1:5385/extensions/spotify/oauth2/callback?x=1",
        }) Check(SpotifyRedirectUri.GetError(new Uri(value)) != null, "Bad redirect accepted: " + value);
        Check(SpotifyRedirectUri.GetError(new Uri(SpotifyRedirectUri.Default)) == null, "Default redirect rejected");
        var session = DispatchProxy.Create<IChatSessionChatAugmentationApi, LogoSessionProxy>();
        var moduleId = Guid.NewGuid();
        await new SpotifyUserInteractionWrapper(session, moduleId).RequestUserInteraction(new Uri("https://accounts.spotify.com/authorize"), false, default);
        Check(((LogoSessionProxy)session).Input?.ModuleId == moduleId, "Chat request missing module logo identity");
        Console.WriteLine("PASS tool cards, editable descriptions, default selection/styles, saved settings, redirect validation and chat logo identity");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}

public class LogoSessionProxy : DispatchProxy
{
    public UserInteractionRequestInput? Input;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name != "RequestUserAction") throw new Exception("Unexpected session access");
        Input = (UserInteractionRequestInput)args![0]!;
        return Task.FromResult<IUserInteractionRequestToken>(null!);
    }
}
