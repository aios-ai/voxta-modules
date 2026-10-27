using System.Reflection;
using System.Text.Json;
using HueApi.Models.Clip;
using Voxta.Abstractions.Authorization;
using Voxta.Abstractions.Encryption;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.Aios.PhilipsHue.Clients;

internal static class HueCredentialChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "HueCredentialChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = DispatchProxy.Create<IModuleOAuthTokenStore, CredentialStoreProxy>();
            var backend = (CredentialStoreProxy)(object)store;
            var encryption = DispatchProxy.Create<ILocalEncryptionProvider, CredentialEncryptionProxy>();
            var user = Guid.NewGuid(); var module = Guid.NewGuid();
            var path = Path.Combine(root, "legacy.json");
            var credentials = new RegisterEntertainmentResult { Ip = "192.0.2.1", Username = "test-hue-secret" };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(credentials));
            var adapter = new HueVoxtaCredentialStore(store, encryption, user, module, () => path);
            var imported = await adapter.LoadAsync(default);
            Check(imported?.Username == credentials.Username && !File.Exists(path), "Legacy import did not complete");
            Check(backend.Saves == 1 && !backend.Values.Values.Single().Token.EncryptedAccessToken.Contains(credentials.Username), "Credential was not encrypted");
            var restarted = new HueVoxtaCredentialStore(store, encryption, user, module, () => throw new Exception("Native credentials depended on legacy path"));
            Check((await restarted.LoadAsync(default))?.Ip == credentials.Ip && backend.Saves == 1, "Native store did not survive adapter recreation");
            Check(await new HueVoxtaCredentialStore(store, encryption, Guid.NewGuid(), module, () => path).LoadAsync(default) == null, "Cross-user credential leak");
            Check(await new HueVoxtaCredentialStore(store, encryption, user, Guid.NewGuid(), () => path).LoadAsync(default) == null, "Cross-module credential leak");
            // Native credentials are authoritative even if a stale legacy file remains.
            await File.WriteAllTextAsync(path, "invalid stale file");
            Check((await restarted.LoadAsync(default))?.Username == credentials.Username && backend.Saves == 1, "Legacy file overrode native credentials");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(credentials));
            backend.FailSave = true;
            try { await new HueVoxtaCredentialStore(store, encryption, user, Guid.NewGuid(), () => path).LoadAsync(default); throw new Exception("Save failure swallowed"); }
            catch (IOException) { }
            Check(File.Exists(path), "Legacy credentials deleted before save succeeded");
            backend.FailSave = false; backend.FailReadback = true;
            try { await new HueVoxtaCredentialStore(store, encryption, user, Guid.NewGuid(), () => path).LoadAsync(default); throw new Exception("Readback failure swallowed"); }
            catch (InvalidOperationException) { }
            Check(File.Exists(path), "Legacy credentials deleted before readback succeeded");
            backend.FailReadback = false;
            var concurrentModule = Guid.NewGuid(); var before = backend.Saves;
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new HueVoxtaCredentialStore(store, encryption, user, concurrentModule, () => path).LoadAsync(default)));
            Check(backend.Saves == before + 1 && !File.Exists(path), "Concurrent startup duplicated credential migration");
            using var cts = new CancellationTokenSource(); cts.Cancel();
            try { await restarted.LoadAsync(cts.Token); throw new Exception("Cancellation swallowed"); } catch (OperationCanceledException) { }
            Console.WriteLine("PASS encrypted native credentials, legacy import/readback safety, isolation, concurrency and cancellation");
        }
        finally { File.Delete(Path.Combine(root, "legacy.json")); Directory.Delete(root); }
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
