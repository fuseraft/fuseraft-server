using fuseraft.Web.Services.Users;

namespace FuseraftWeb.Tests;

public sealed class WorkspaceAccessTests
{
    private static WebUser User(bool admin, params string[] roots) => new()
    {
        Id = "u1", UserName = "u", Home = "/h", IsAdmin = admin, WorkspaceRoots = [.. roots],
    };

    [Theory]
    [InlineData("/srv/bob", true)]
    [InlineData("/srv/bob/project/o.yaml", true)]
    [InlineData("/srv/bob/../alice", false)]
    [InlineData("/srv/bobby", false)]
    [InlineData("/etc/passwd", false)]
    public void NonAdmin_StaysUnderTheirRoots(string path, bool allowed)
    {
        Assert.Equal(allowed, WorkspaceAccess.For(User(false, "/srv/bob")).Allows(path));
    }

    [Fact]
    public void Admin_GoesAnywhere()
    {
        Assert.True(WorkspaceAccess.For(User(true, "/srv/bob")).Allows("/etc"));
    }

    [Fact]
    public void Check_ResolvesRelativeToTheBase()
    {
        var access = WorkspaceAccess.For(User(false, "/srv/bob"));

        Assert.Equal(Path.GetFullPath("/srv/bob/w/o.yaml"), access.Check("o.yaml", "/srv/bob/w"));
        Assert.Throws<UnauthorizedAccessException>(() => access.Check("../../../etc/x", "/srv/bob/w"));
    }

    [Fact]
    public void Picker_CantStepAboveARoot()
    {
        var access = WorkspaceAccess.For(User(false, "/srv/bob"));

        Assert.False(access.CanLeave("/srv/bob"));
        Assert.True(access.CanLeave("/srv/bob/project"));
    }
}
