using Microsoft.Extensions.Options;
using fuseraft.Web.Services;
using fuseraft.Web.Services.Users;

namespace FuseraftWeb.Tests;

public sealed class UserStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fuseraft-web-users").FullName;

    static UserStoreTests() => PasswordHashing.Iterations = 1_000;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private UserStore Store() => new(
        Options.Create(new WebOptions { DataDirectory = _dir }),
        Options.Create(new AuthOptions()));

    [Fact]
    public void FirstAccount_IsAnAdminWithTheDefaultHome_AndEndsSetup()
    {
        var store = Store();
        Assert.NotNull(store.SetupCode);

        var first  = store.Create("Alice", "correct horse battery", isAdmin: false);
        var second = store.Create("bob", "correct horse battery", isAdmin: false);

        Assert.Equal("alice", first.UserName);
        Assert.True(first.IsAdmin);
        Assert.Equal(FuseraftHome.Root, first.Home);
        Assert.False(second.IsAdmin);
        Assert.Equal(Path.Combine(_dir, "homes", second.Id), second.Home);
        Assert.Equal([Path.Combine(_dir, "workspaces", "bob")], second.WorkspaceRoots);
        Assert.Null(store.SetupCode);
    }

    [Fact]
    public void Passwords_AreCheckedAndNeverStoredPlain()
    {
        var store = Store();
        store.Create("alice", "correct horse battery", isAdmin: true);

        Assert.NotNull(store.CheckPassword("ALICE", "correct horse battery"));
        Assert.Null(store.CheckPassword("alice", "wrong"));
        Assert.Null(store.CheckPassword("nobody", "correct horse battery"));
        Assert.DoesNotContain("correct horse", File.ReadAllText(Path.Combine(_dir, "users.json")));
        Assert.Throws<ArgumentException>(() => store.Create("bob", "short", isAdmin: false));
    }

    [Fact]
    public void DisabledAccount_CantSignIn()
    {
        var store = Store();
        store.Create("alice", "correct horse battery", isAdmin: true);
        var bob = store.Create("bob", "correct horse battery", isAdmin: false);

        store.Update(bob.Id, u => u.Disabled = true, signOut: true);

        Assert.Null(store.CheckPassword("bob", "correct horse battery"));
    }

    [Fact]
    public void LastAdmin_CantBeDemotedDisabledOrRemoved()
    {
        var store = Store();
        var alice = store.Create("alice", "correct horse battery", isAdmin: true);

        Assert.Throws<InvalidOperationException>(() => store.Update(alice.Id, u => u.IsAdmin = false));
        Assert.Throws<InvalidOperationException>(() => store.Update(alice.Id, u => u.Disabled = true));
        Assert.Throws<InvalidOperationException>(() => store.Delete(alice.Id));
        Assert.True(store.Find(alice.Id)!.IsAdmin);

        var bob = store.Create("bob", "correct horse battery", isAdmin: true);
        store.Update(alice.Id, u => u.IsAdmin = false);
        Assert.False(store.Find(alice.Id)!.IsAdmin);
        Assert.True(store.Find(bob.Id)!.IsAdmin);
    }

    [Fact]
    public void SetPassword_ChangesTheStamp()
    {
        var store = Store();
        var alice = store.Create("alice", "correct horse battery", isAdmin: true);
        var stamp = alice.SecurityStamp;

        store.SetPassword(alice.Id, "another long password");

        Assert.NotEqual(stamp, store.Find(alice.Id)!.SecurityStamp);
        Assert.NotNull(store.CheckPassword("alice", "another long password"));
    }

    [Fact]
    public void ChangesFromAnotherProcess_AreSeen_OnTheSameObjects()
    {
        var server = Store();
        var alice  = server.Create("alice", "correct horse battery", isAdmin: true);
        var bob    = server.Create("bob", "correct horse battery", isAdmin: false);

        // fuseraft-web users, run while the server is up
        var cli = Store();
        cli.Update(bob.Id, u => u.Disabled = true, signOut: true);
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "users.json"), DateTime.UtcNow.AddSeconds(5));
        cli.Create("carol", "correct horse battery", isAdmin: false);

        var seenBob = server.Find(bob.Id)!;
        Assert.True(seenBob.Disabled);
        Assert.Same(bob, seenBob);
        Assert.NotNull(server.FindByName("carol"));
        Assert.Same(alice, server.Find(alice.Id));
    }

    [Fact]
    public void Names_AreValidated()
    {
        var store = Store();
        Assert.NotNull(store.ValidateNewName("../etc"));
        Assert.NotNull(store.ValidateNewName(""));
        Assert.Null(store.ValidateNewName("first.last"));
        store.Create("first.last", "correct horse battery", isAdmin: true);
        Assert.Contains("already", store.ValidateNewName("First.Last"));
    }

    [Fact]
    public void SetupCode_IsCheckedExactly()
    {
        var store = Store();
        Assert.True(store.CheckSetupCode(store.SetupCode));
        Assert.False(store.CheckSetupCode("nope"));
        Assert.False(store.CheckSetupCode(null));
    }

    [Fact]
    public void Commands_ManageAccounts()
    {
        var store = Store();
        var output = new StringWriter();
        string Pw(string _) => "correct horse battery";

        Assert.Equal(0, UsersCommand.Execute(store, ["add", "alice", "--admin"], output, Pw));
        Assert.Equal(0, UsersCommand.Execute(store, ["add", "bob"], output, Pw));
        Assert.Equal(0, UsersCommand.Execute(store, ["disable", "bob"], output, Pw));
        Assert.Equal(0, UsersCommand.Execute(store, ["admin", "bob", "on"], output, Pw));
        Assert.Equal(1, UsersCommand.Execute(store, ["frobnicate"], output, Pw));

        Assert.True(store.FindByName("bob") is { Disabled: true, IsAdmin: true });
        Assert.Throws<ArgumentException>(() => UsersCommand.Execute(store, ["add", "carol"], output, _ => "short"));
    }
}
