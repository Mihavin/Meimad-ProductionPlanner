using System.Globalization;
using System.Security.Cryptography;

namespace Meimad.Planner.Server.Application.Accounts;

/// <summary>
/// Stores passwords as PBKDF2-SHA256 hashes with a random salt, in the form
/// <c>pbkdf2-sha256$iterations$salt$hash</c> (Base64). A password is never stored or logged.
/// </summary>
internal static class PasswordHasher
{
    internal const int MinimumLength = 6;
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Scheme = "pbkdf2-sha256";

    internal static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Scheme, Iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    internal static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations < 1)
        {
            return false;
        }
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>A password the rules accept: at least six characters, not only spaces.</summary>
    internal static void Validate(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinimumLength)
            throw new AccountException(StatusCodes.Status422UnprocessableEntity, "password_too_short",
                $"The password needs at least {MinimumLength} characters.");
        if (password.Length > 200)
            throw new AccountException(StatusCodes.Status422UnprocessableEntity, "password_too_long",
                "The password may have at most 200 characters.");
    }
}
