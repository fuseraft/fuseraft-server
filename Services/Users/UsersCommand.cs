using Microsoft.Extensions.Options;

namespace fuseraft.Web.Services.Users;

/// <summary><c>fuseraft-web users …</c>: manage accounts from a shell, for the first admin or a locked-out one.</summary>
public static class UsersCommand
{
    private const string Usage = """
        Usage:
          fuseraft-web users list
          fuseraft-web users add <name> [--admin]     prompts for a password
          fuseraft-web users passwd <name>            prompts for a new password
          fuseraft-web users admin <name> on|off
          fuseraft-web users disable <name> | enable <name>
          fuseraft-web users remove <name>
        Set FUSERAFT_WEB_PASSWORD to skip the prompt.
        """;

    public static int Run(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var store = new UserStore(
            Options.Create(config.GetSection("Fuseraft").Get<WebOptions>() ?? new WebOptions()),
            Options.Create(config.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions()));
        try
        {
            return Execute(store, args, Console.Out);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    internal static int Execute(UserStore store, string[] args, TextWriter output, Func<string, string>? readPassword = null)
    {
        readPassword ??= ReadPassword;
        WebUser Named(string name) => store.FindByName(name) ?? throw new ArgumentException($"No account named '{name}'.");

        switch (args)
        {
            case ["list"]:
                foreach (var u in store.All)
                    output.WriteLine($"{u.UserName,-24} {(u.IsAdmin ? "admin" : "user"),-6} {(u.Disabled ? "disabled" : "")}");
                if (store.IsEmpty) output.WriteLine("No accounts.");
                return 0;

            case ["add", var name, .. var rest] when rest is [] or ["--admin"]:
                var user = store.Create(name, NewPassword(readPassword), isAdmin: rest is ["--admin"]);
                output.WriteLine($"Created {(user.IsAdmin ? "admin" : "user")} '{user.UserName}'.");
                return 0;

            case ["passwd", var name]:
                store.SetPassword(Named(name).Id, NewPassword(readPassword));
                output.WriteLine($"Changed the password of '{name}' and ended its sign-ins.");
                return 0;

            case ["admin", var name, var state] when state is "on" or "off":
                store.Update(Named(name).Id, u => u.IsAdmin = state == "on");
                output.WriteLine($"'{name}' is {(state == "on" ? "now" : "no longer")} an admin.");
                return 0;

            case [var verb, var name] when verb is "disable" or "enable":
                store.Update(Named(name).Id, u => u.Disabled = verb == "disable", signOut: verb == "disable");
                output.WriteLine($"{(verb == "disable" ? "Disabled" : "Enabled")} '{name}'.");
                return 0;

            case ["remove", var name]:
                store.Delete(Named(name).Id);
                output.WriteLine($"Removed '{name}'. Its fuseraft home was left on disk.");
                return 0;

            default:
                output.WriteLine(Usage);
                return args is [] or ["help"] or ["--help"] ? 0 : 1;
        }
    }

    private static string NewPassword(Func<string, string> read)
    {
        if (Environment.GetEnvironmentVariable("FUSERAFT_WEB_PASSWORD") is { Length: > 0 } fromEnv) return fromEnv;
        var password = read("Password: ");
        if (PasswordHashing.Validate(password) is { } weak) throw new ArgumentException(weak);
        if (read("Again: ") != password) throw new ArgumentException("The passwords don't match.");
        return password;
    }

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var chars = new List<char>();
        while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); }
            else if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string([.. chars]);
    }
}
