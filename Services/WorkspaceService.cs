namespace fuseraft.Server.Services;

public sealed class WorkspaceService
{
    private string _current = string.Empty;

    public string Current => _current;

    public event Action? Changed;

    public void Set(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == _current) return;
        _current = path.Trim();
        Changed?.Invoke();
    }
}
