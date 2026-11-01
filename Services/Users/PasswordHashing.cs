using System.Security.Cryptography;

namespace fuseraft.Web.Services.Users;

/// <summary>PBKDF2-SHA256 password hashes, stored as <c>pbkdf2-sha256$iterations$salt$hash</c>.</summary>
public static class PasswordHashing
{
    private const string Scheme = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>OWASP's 2023 recommendation for PBKDF2-SHA256; tests lower it.</summary>
    internal static int Iterations { get; set; } = 600_000;

    public const int MinimumLength = 10;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        var parts = stored?.Split('$');
        if (parts is not [Scheme, var iter, var salt, var hash] || !int.TryParse(iter, out var iterations) || iterations < 1)
            return false;
        try
        {
            var expected = Convert.FromBase64String(hash);
            var actual   = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), iterations,
                HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Null when <paramref name="password"/> is acceptable, otherwise why not.</summary>
    public static string? Validate(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinimumLength
            ? $"Passwords must be at least {MinimumLength} characters."
            : null;
}
