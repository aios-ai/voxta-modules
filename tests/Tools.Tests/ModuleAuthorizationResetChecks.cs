using System.Reflection;
using Voxta.Abstractions.Model;
using Voxta.Abstractions.Repositories;
using Voxta.Modules.Aios.Tools;
using Module = Voxta.Abstractions.Model.Module;

internal static class ModuleAuthorizationResetChecks
{
    public static async Task RunAsync()
    {
        var user = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var moduleId = Guid.NewGuid();
        var otherModule = Guid.NewGuid();
        var module = new Module { Id = moduleId, ServiceName = "Aios.Test", Configuration = new()
        {
            ["Account"] = Guid.NewGuid().ToString(), ["ClientSecret"] = "encrypted-secret", ["Tools"] = "settings"
        }};
        var rows = new List<(Guid User, Token Token)>
        {
            (user, Token(moduleId, "Account")), (user, Token(moduleId, "Account")),
            (user, Token(moduleId, "OtherAccount")), (user, Token(otherModule, "Account")),
            (otherUser, Token(moduleId, "Account"))
        };
        var saves = 0;
        var failSave = false;
        var failRead = false;
        var modules = Proxy<IModuleRepository>((method, args) => method.Name switch
        {
            "GetByIdAsync" => (Guid)args[0]! == moduleId ? Task.FromResult<Module?>(module) : Task.FromResult<Module?>(null),
            "UpdateAsync" => Task.CompletedTask,
            _ => throw new Exception(method.Name)
        });
        var factory = Proxy<IUnitOfWorkFactory>((method, args) =>
        {
            Check(method.Name == "CreateUnitOfWork", "Must use a user-scoped unit of work");
            var scopedUser = (Guid)args[0]!;
            var tokens = Proxy<ITokenRepository>((m, a) =>
            {
                if (m.Name == "GetByModuleAsync")
                {
                    if (failRead) throw new IOException("Storage unavailable");
                    return Task.FromResult(rows.Where(r => r.User == scopedUser && r.Token.ModuleId == (Guid)a[0]!).Select(r => r.Token).ToArray());
                }
                if (m.Name == "DeleteAsync")
                {
                    rows.RemoveAll(r => r.User == scopedUser && r.Token == (Token)a[0]!);
                    return Task.CompletedTask;
                }
                throw new Exception(m.Name);
            });
            return Proxy<IUnitOfWork>((m, a) => m.Name switch
            {
                "get_Modules" => modules,
                "get_Tokens" => tokens,
                "SaveChangesAsync" => failSave ? throw new IOException("Save failed") : Task.FromResult(++saves),
                "DisposeAsync" => ValueTask.CompletedTask,
                _ => throw new Exception(m.Name)
            });
        });
        Task Reset(Guid id, string service, CancellationToken ct = default) =>
            ModuleAuthorizationReset.ClearAsync(factory, user, id, service, "Account", ct);

        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Throws<OperationCanceledException>(() => Reset(moduleId, "Aios.Test", canceled.Token));
        await Throws<InvalidOperationException>(() => Reset(otherModule, "Aios.Test"));
        await Throws<InvalidOperationException>(() => Reset(moduleId, "Aios.Wrong"));
        Check(saves == 0 && rows.Count == 5, "Invalid actions changed storage");
        failRead = true;
        await Throws<IOException>(() => Reset(moduleId, "Aios.Test"));
        Check(module.Configuration.ContainsKey("Account") && saves == 0, "Read failure cleared settings");
        failRead = false;
        await Reset(moduleId, "Aios.Test");
        Check(rows.Count == 3 && rows.All(r => r.User != user || r.Token.ModuleId != moduleId || r.Token.Identifier != "Account"), "Wrong rows removed");
        Check(saves == 1 && !module.Configuration.ContainsKey("Account") && module.Configuration["ClientSecret"] == "encrypted-secret" && module.Configuration["Tools"] == "settings", "Unrelated settings changed");
        await Reset(moduleId, "Aios.Test");
        Check(rows.Count == 3 && saves == 2, "Repeated reset was not harmless");
        failSave = true;
        await Throws<IOException>(() => Reset(moduleId, "Aios.Test"));
        Console.WriteLine("PASS authorization reset: damaged/duplicate credentials, user/module/field isolation, preserved settings, repeated reset, failures and cancellation");
    }

    private static Token Token(Guid module, string field) => new()
    {
        Id = Guid.NewGuid(), ModuleId = module, Identifier = field,
        AccessToken = "damaged-ciphertext", Expires = DateTimeOffset.MaxValue
    };

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, ResetProxy>();
        ((ResetProxy)(object)proxy).Handler = invoke;
        return proxy;
    }
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}

public class ResetProxy : DispatchProxy
{
    public Func<MethodInfo, object?[], object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        foreach (var ct in args!.OfType<CancellationToken>()) ct.ThrowIfCancellationRequested();
        return Handler(method!, args!);
    }
}
