namespace PaperTrading.Api.Infrastructure;

/// <summary>
/// Loads KEY=VALUE lines from a .env file into environment variables, so users can
/// keep their own API keys in one git-ignored file. Real environment variables win.
/// </summary>
public static class DotEnv
{
    public static void Load()
    {
        foreach (var path in Candidates())
        {
            if (!File.Exists(path)) continue;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("export ")) line = line[7..].Trim();
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim().Trim('"', '\'');
                if (value.Length == 0) continue;
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                    Environment.SetEnvironmentVariable(key, value);
            }
            return;
        }
    }

    // Look in the current directory and its parents, so it works from the repo root or the project folder.
    private static IEnumerable<string> Candidates()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; dir is not null && i < 4; i++, dir = dir.Parent)
            yield return Path.Combine(dir.FullName, ".env");
    }
}
