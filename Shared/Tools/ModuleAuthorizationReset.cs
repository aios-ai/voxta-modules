using Voxta.Abstractions.Repositories;

namespace Voxta.Modules.Aios.Tools;

/// <summary>Forget one module account without decrypting potentially damaged credentials.</summary>
internal static class ModuleAuthorizationReset
{
    public static async Task ClearAsync(IUnitOfWorkFactory factory, Guid userId, Guid moduleId,
        string serviceName, string field, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var uow = factory.CreateUnitOfWork(userId);
        var module = await uow.Modules.GetByIdAsync(moduleId, cancellationToken)
            ?? throw new InvalidOperationException("The module no longer exists. Reopen module settings.");
        if (module.ServiceName != serviceName)
            throw new InvalidOperationException("The authorization does not belong to this module.");

        // The token-store API lists values without row IDs. Use the SDK's user-scoped
        // repositories to remove this field's rows and its configuration pointer together.
        foreach (var token in await uow.Tokens.GetByModuleAsync(moduleId, cancellationToken))
            if (token.Identifier == field)
                await uow.Tokens.DeleteAsync(token);
        module.Configuration.Remove(field);
        await uow.Modules.UpdateAsync(module);
        await uow.SaveChangesAsync(cancellationToken);
    }
}
