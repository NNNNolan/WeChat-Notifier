using Microsoft.Win32;
using System.IO;
using System.Text.RegularExpressions;
using System.Text;

namespace WeChat_Notifier.Infrastructure.WeChat.Database;

/// <summary>
/// Finds the xwechat_files directory that holds the logged-in account's
/// db_storage. Config files, the registry and the usual default locations are
/// tried in that order, mirroring wechatauto-replica's discovery.
/// </summary>
public static class WeChatDbLocator
{
    private static readonly Regex WindowsPath = new(
        @"[A-Za-z]:[\\/][^\s\x00-\x1f""']+", RegexOptions.Compiled);

    public static string? FindAccountRoot()
    {
        foreach (var root in CandidateRoots())
        {
            var hit = Locate(root);
            if (hit != null) return hit;

            var text = FirstTextFile(root);
            if (text == null) continue;
            var path = PathFromConfig(text);
            hit = Locate(path);
            if (hit != null) return hit;
        }

        foreach (var root in RegistryRoots())
        {
            var hit = Locate(root);
            if (hit != null) return hit;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[] { Path.Combine(profile, "Documents"), profile })
        {
            var hit = Locate(root);
            if (hit != null) return hit;
        }

        return null;
    }

    /// <summary>Account directories under <paramref name="accountRoot"/>, newest db first.</summary>
    public static List<string> Accounts(string accountRoot)
    {
        try
        {
            return Directory.EnumerateDirectories(accountRoot)
                .Where(d => Directory.Exists(Path.Combine(d, "db_storage")))
                .OrderByDescending(NewestDb)
                .ToList();
        }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
    }

    private static DateTime NewestDb(string accountDir)
    {
        try
        {
            return Directory.EnumerateFiles(Path.Combine(accountDir, "db_storage"),
                    "*.db", SearchOption.AllDirectories)
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
        }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private static IEnumerable<string> CandidateRoots()
    {
        foreach (var env in new[] { "APPDATA", "LOCALAPPDATA" })
        {
            var baseDir = Environment.GetEnvironmentVariable(env);
            if (string.IsNullOrWhiteSpace(baseDir)) continue;
            foreach (var sub in new[] { "xwechat", @"xwechat\config", "WeChat" })
                yield return Path.Combine(baseDir, "Tencent", sub);
        }
    }

    private static IEnumerable<string> RegistryRoots()
    {
        foreach (var sub in new[]
                 {
                     @"Software\Tencent\xwechat",
                     @"Software\Tencent\xwechat\config",
                     @"Software\Tencent\WeChat"
                 })
        {
            using var key = Registry.CurrentUser.OpenSubKey(sub);
            if (key == null) continue;
            foreach (var name in key.GetValueNames())
            {
                var low = name.ToLowerInvariant();
                if (!low.Contains("path") && !low.Contains("dir") && !low.Contains("save"))
                    continue;
                if (key.GetValue(name) is string s && !string.IsNullOrWhiteSpace(s))
                    yield return s.Trim();
            }
        }
    }

    /// <summary>
    /// Extracts a data path from an .ini / JSON file. Some clients write a bare
    /// path, others embed it in JSON or arbitrary text.
    /// </summary>
    public static string? PathFromConfig(string content)
    {
        content = content.Trim().TrimStart('\uFEFF');
        if (content.Length == 0) return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var name in new[]
                         {
                             "dataDir", "data_dir", "fileSavePath", "savePath",
                             "path", "defaultFileSavePath"
                         })
                {
                    if (doc.RootElement.TryGetProperty(name, out var v)
                        && v.ValueKind == System.Text.Json.JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(v.GetString()))
                        return v.GetString()!.Trim();
                }
            }
        }
        catch (System.Text.Json.JsonException) { }

        if (WindowsPath.IsMatch(content))
        {
            var m = WindowsPath.Match(content);
            return Normalize(m.Value);
        }
        return null;
    }

    /// <summary>
    /// Finds the parent of account directories. Accepts both
    /// &lt;root&gt;/xwechat_files/&lt;account&gt;/db_storage and
    /// &lt;root&gt;/&lt;account&gt;/db_storage.
    /// </summary>
    public static string? Locate(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        root = Normalize(root);
        if (!Directory.Exists(root)) return null;

        foreach (var cand in new[]
                 {
                     root,
                     Path.Combine(root, "xwechat_files"),
                     Path.Combine(root, "WeChat Files"),
                     Path.Combine(root, "xwechat_files_data")
                 })
        {
            if (!Directory.Exists(cand)) continue;
            try
            {
                if (Directory.EnumerateDirectories(cand)
                    .Any(d => Directory.Exists(Path.Combine(d, "db_storage"))))
                    return cand;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private static string Normalize(string path)
    {
        var p = Path.GetFullPath(path.Trim());
        // "d:" means the process's current directory on D:, not the drive root.
        if (Regex.IsMatch(p, @"^[A-Za-z]:$")) p += Path.DirectorySeparatorChar;
        return p;
    }

    private static string? FirstTextFile(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                try { return File.ReadAllText(f, Encoding.UTF8); }
                catch (DecoderFallbackException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}
