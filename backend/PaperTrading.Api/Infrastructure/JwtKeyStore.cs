using System.Security.Cryptography;

namespace PaperTrading.Api.Infrastructure;

public static class JwtKeyStore
{
    /// <summary>Uses the configured key, or creates a random one on first run and keeps it beside the database.</summary>
    public static string Resolve(string? configuredKey, string dataDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            if (configuredKey.Length < 32) throw new InvalidOperationException("Jwt:Key must be at least 32 characters.");
            return configuredKey;
        }

        var path = Path.Combine(dataDirectory, "jwt.key");
        if (File.Exists(path)) return File.ReadAllText(path).Trim();

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        File.WriteAllText(path, key);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }
}
