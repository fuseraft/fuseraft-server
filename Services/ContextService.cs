using fuseraft.Infrastructure;

namespace fuseraft.Server.Services;

public sealed class ContextService
{
    public Task<ContextIndex> GetIndexAsync(string workspaceDir) =>
        StoreFor(workspaceDir).LoadIndexAsync();

    public Task AddAsync(string workspaceDir, string sourcePath, string name, string? description = null) =>
        StoreFor(workspaceDir).AddAsync(sourcePath, name, description);

    public Task RemoveAsync(string workspaceDir, string name) =>
        StoreFor(workspaceDir).RemoveAsync(name);

    private static ContextStore StoreFor(string workspaceDir) =>
        new(Path.Combine(workspaceDir, ContextStore.DefaultContextDir));
}
