using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Voxta.Abstractions.Caching;
using Voxta.Abstractions.Modules;
using Voxta.Abstractions.Repositories;
using Voxta.Model;

namespace Voxta.Modules.Aios.KeyboardShortcuts;

internal sealed class ShortcutPacksEndpoints(IUnitOfWorkFactory unitOfWorkFactory, IUserCache userCache) : IModuleHttpEndpoints
{
    internal const string BasePath = "/api/extensions/keyboard-shortcuts";
    private const int MaxRequestBytes = 1024 * 1024;
    public string RoutePrefix => "keyboard-shortcuts";
    public ModuleHttpAccess Access => ModuleHttpAccess.SignedIn;

    public async Task<ModuleHttpResponse?> HandleAsync(ModuleHttpRequest request, CancellationToken cancellationToken)
    {
        var user = await userCache.GetByIdAsync(request.UserId, cancellationToken);
        if (user?.Role != VoxtaUserRoles.Admin) return Error(403, "An administrator can manage shortcut groups.");

        if (request.Method == "GET")
        {
            var asset = request.Path switch
            {
                "" => ("index.html", "text/html; charset=utf-8"),
                "groups.css" => ("groups.css", "text/css; charset=utf-8"),
                "groups.js" => ("groups.js", "text/javascript; charset=utf-8"),
                _ => (null, null),
            };
            if (asset.Item1 is { } name) return await AssetAsync(name, asset.Item2!, cancellationToken);
            if (request.Path != "groups") return null;
        }
        else if (request.Method != "POST" || request.Path is not ("preview" or "import")) return null;

        try
        {
            if (request.Method == "GET") return await HandleDataAsync(request, null, cancellationToken);
            if (!string.Equals(request.ContentType?.Split(';', 2)[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
                return Error(415, "Send a JSON shortcut pack.");

            var buffer = new byte[MaxRequestBytes + 1];
            var size = await request.Body.ReadAtLeastAsync(buffer.AsMemory(), buffer.Length, false, cancellationToken);
            if (size > MaxRequestBytes) return Error(413, "The shortcut pack exceeds 1 MB.");
            using var body = JsonDocument.Parse(buffer.AsMemory(0, size));
            if (request.Path == "preview") return await HandleDataAsync(request, body.RootElement, cancellationToken);

            await ShortcutSettings.WriteLock.WaitAsync(cancellationToken);
            try
            {
                return await HandleDataAsync(request, body.RootElement, cancellationToken);
            }
            finally
            {
                ShortcutSettings.WriteLock.Release();
            }
        }
        catch (JsonException)
        {
            return Error(400, request.Method == "GET" ? "The saved shortcut list is invalid." : "The saved list or the uploaded pack is invalid JSON.");
        }
        catch (InvalidDataException exception)
        {
            return Error(400, exception.Message);
        }
    }

    private async Task<ModuleHttpResponse> HandleDataAsync(ModuleHttpRequest request, JsonElement? body, CancellationToken cancellationToken)
    {
        await using var work = unitOfWorkFactory.CreateUnitOfWork(request.UserId);
        var modules = await work.Modules.GetManyByServiceNameAsync(VoxtaModule.ServiceName, cancellationToken);
        if (modules.Length != 1) return Error(409, "Install a single Keyboard Shortcuts module before managing groups.");
        var module = modules[0];
        var raw = module.Configuration.GetValueOrDefault(ShortcutConfiguration.FieldName, "");
        var rawGroups = module.Configuration.GetValueOrDefault(ShortcutSettings.GroupsField, "");
        var settings = ShortcutSettings.Read(raw, rawGroups);
        if (body == null)
        {
            var groups = settings.Groups.OrderBy(group => group.Id, StringComparer.Ordinal)
                .Select(group => new { name = (string?)group.Id, label = group.Name, group.Enabled,
                    pack = ShortcutPacks.Export(group.Rows.Select(row => ShortcutSettings.WithGroup(row, group.Name))) }).ToList();
            if (settings.Standalone.Count > 0)
                groups.Insert(0, new { name = (string?)null, label = "Standalone", Enabled = true,
                    pack = ShortcutPacks.Export(settings.Standalone.Select(row => ShortcutSettings.WithGroup(row, null))) });
            return ModuleHttpResponse.Json(new { groups });
        }

        var revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { raw, rawGroups })));
        var pack = body.Value;
        if (request.Path == "import")
        {
            if (pack.ValueKind != JsonValueKind.Object || !pack.TryGetProperty("revision", out var expected)
                || expected.ValueKind != JsonValueKind.String || expected.GetString() != revision)
                return Error(409, "Shortcuts changed since the preview. Select the file again to review the updated merge.");
            if (!pack.TryGetProperty("pack", out pack)) return Error(400, "Choose a shortcut pack.");
        }

        var result = ShortcutPacks.Preview(settings, pack);
        if (request.Path == "import" && result.Added.Length > 0)
        {
            foreach (var row in result.Added) settings.Add(row);
            settings.SaveTo(module.Configuration);
            await work.Modules.UpdateAsync(module);
            await work.SaveChangesAsync(cancellationToken);
        }

        return ModuleHttpResponse.Json(new { revision, added = result.Added, result.Duplicates, result.Conflicts });
    }

    private static ModuleHttpResponse Error(int code, string error) =>
        new(code, "application/json", JsonSerializer.Serialize(new { error }));

    private static async Task<ModuleHttpResponse> AssetAsync(string name, string type, CancellationToken cancellationToken)
    {
        using var stream = typeof(ShortcutPacksEndpoints).Assembly.GetManifestResourceStream(
            $"{typeof(ShortcutPacksEndpoints).Namespace}.PacksUi.{name}")!;
        using var reader = new StreamReader(stream);
        return new ModuleHttpResponse(200, type, await reader.ReadToEndAsync(cancellationToken));
    }
}
