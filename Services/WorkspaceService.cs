using fuseraft.Web.Services.Users;

namespace fuseraft.Web.Services;

/// <summary>The workspace this tab last picked, shared by its pages. A non-admin starts in their first root.</summary>
public sealed class WorkspaceService(WorkspaceAccess access)
{
    private string _current = access.Roots.Count > 0 ? access.DefaultDirectory : string.Empty;

    public string Current => _current;

    public event Action? Changed;

    public void Set(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == _current || !access.Allows(path)) return;
        _current = path.Trim();
        Changed?.Invoke();
    }
}
