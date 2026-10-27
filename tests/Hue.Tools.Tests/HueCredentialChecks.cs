using System.Reflection;
using HueApi.Models.Clip;
using Voxta.Abstractions.Authorization;
using Voxta.Abstractions.Encryption;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.Aios.PhilipsHue.Clients;

internal static class HueCredentialChecks
{
    public static async Task RunAsync()
    {
        var store = DispatchProxy.Create<IModuleOAuthTokenStore, CredentialStoreProxy>();
        var backend = (CredentialStoreProxy)(object)store;
        var encryption = DispatchProxy.Create<ILocalEncryptionProvider, CredentialEncryptionProxy>();
        var user = Guid.NewGuid(); var module = Guid.NewGuid();
        var credentials = new RegisterEntertainmentResult { Ip = "192.0.2.1", Username = "test-hue-secret" };
        var adapter = new HueVoxtaCredentialStore(store, encryption, user, module);
        Check(await adapter.LoadAsync(default) == null && backend.Saves == 0, "Fresh installation should have no credentials");
        await adapter.SaveAsync(credentials, default);
        Check(backend.Saves == 1 && !backend.Values.Values.Single().Token.EncryptedAccessToken.Contains(credentials.Username), "Credential was not encrypted");
        var restarted = new HueVoxtaCredentialStore(store, encryption, user, module);
        Check((await restarted.LoadAsync(default))?.Username == credentials.Username && backend.Saves == 1, "Native credential reload");
        Check(await new HueVoxtaCredentialStore(store, encryption, Guid.NewGuid(), module).LoadAsync(default) == null, "Cross-user credential leak");
        Check(await new HueVoxtaCredentialStore(store, encryption, user, Guid.NewGuid()).LoadAsync(default) == null, "Cross-module credential leak");
        backend.FailSave = true;
        try { await adapter.SaveAsync(credentials, default); throw new Exception("Save failure swallowed"); } catch (IOException) { }
        backend.FailSave = false; backend.FailReadback = true;
        try { await adapter.SaveAsync(credentials, default); throw new Exception("Readback failure swallowed"); } catch (InvalidOperationException) { }
        backend.FailReadback = false;
        using var cts = new CancellationTokenSource(); cts.Cancel();
        try { await restarted.LoadAsync(cts.Token); throw new Exception("Cancellation swallowed"); } catch (OperationCanceledException) { }
        Console.WriteLine("PASS fresh-install native credentials, encryption, readback, isolation and cancellation");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}

public class CredentialStoreProxy : DispatchProxy
{
    public readonly Dictionary<(Guid User, Guid Module, string Field), (Guid Id, AccessTokenInfo Token)> Values = [];
    public int Saves;
    public bool FailSave, FailReadback;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        foreach (var token in args!.OfType<CancellationToken>()) token.ThrowIfCancellationRequested();
        var user = (Guid)args![0]!;
        switch (method!.Name)
        {
            case "ListByModuleAsync":
                return Task.FromResult<IReadOnlyDictionary<string, AccessTokenInfo>>(Values.Where(p => p.Key.User == user && p.Key.Module == (Guid)args[1]!).ToDictionary(p => p.Key.Field, p => p.Value.Token));
            case "SaveAsync":
                if (FailSave) throw new IOException("Simulated store failure");
                var id = (Guid?)args[3] ?? Guid.NewGuid();
                Values[(user, (Guid)args[1]!, (string)args[2]!)] = (id, (AccessTokenInfo)args[4]!); Saves++;
                return Task.FromResult(id);
            case "GetAsync":
                return Task.FromResult<AccessTokenInfo?>(FailReadback ? null : Values.Where(p => p.Key.User == user && p.Value.Id == (Guid)args[1]!).Select(p => p.Value.Token).SingleOrDefault());
            default: throw new Exception("Unexpected credential operation " + method.Name);
        }
    }
}
public class CredentialEncryptionProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "Encrypt" => "encrypted:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes((string)args![0]!)),
        "Decrypt" => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(((string)args![0]!)["encrypted:".Length..])),
        _ => throw new Exception("Unexpected encryption operation"),
    };
}
