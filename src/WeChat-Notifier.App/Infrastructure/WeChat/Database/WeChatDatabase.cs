using Microsoft.Data.Sqlite;
using System.IO;
using System.Security.Cryptography;
using WeChat_Notifier.Domain;

namespace WeChat_Notifier.Infrastructure.WeChat.Database;

/// <summary>
/// Read-only access to session.db for conversations, contact.db for contacts,
/// and bizchat.db for enterprise child names and mute flags.
/// Message databases are deliberately not opened.
/// </summary>
public sealed class WeChatDatabase : IDisposable
{
    private readonly string _accountDir;
    private readonly object _gate = new();
    private readonly string _workDir;
    private readonly Dictionary<string, byte[]> _keys;
    private sealed record Snapshot(string Path, string Signature);
    private readonly Dictionary<string, Snapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _log;

    public string Account { get; }
    public string SessionDbPath { get; }
    public string ContactDbPath { get; }
    public string? BizChatDbPath { get; }
    public string? Wxid { get; }

    private WeChatDatabase(string accountDir, string workDir, Dictionary<string, byte[]> keys, Action<string>? log)
    {
        _accountDir = accountDir;
        _workDir = workDir;
        _keys = keys;
        _log = log;
        Account = Path.GetFileName(accountDir.TrimEnd(Path.DirectorySeparatorChar));
        Wxid = System.Text.RegularExpressions.Regex.Replace(Account, @"_\w{4}$", "");

        var storage = Path.Combine(accountDir, "db_storage");
        SessionDbPath = FindDb(storage, "session.db");
        ContactDbPath = FindDb(storage, "contact.db");
        BizChatDbPath = FindOptionalDb(storage, "bizchat.db");
    }

    /// <summary>
    /// Picks the account whose session/contact and available bizchat databases
    /// authenticate with the extracted keys. This handles the
    /// stale second account directory seen on multi-account machines.
    /// </summary>
    public static WeChatDatabase Open(Action<string>? log = null)
    {
        var root = WeChatDbLocator.FindAccountRoot();
        if (root == null) throw new InvalidOperationException("未找到微信数据库目录");

        var failures = new List<string>();
        foreach (var accountDir in WeChatDbLocator.Accounts(root))
        {
            try
            {
                var extractor = new WeChatKeyExtractor(accountDir, log: log);
                var keys = extractor.LoadOrExtract();
                if (keys.Count == 0)
                {
                    failures.Add($"{Path.GetFileName(accountDir)}: 未提取到密钥");
                    continue;
                }

                log?.Invoke($"{Path.GetFileName(accountDir)}: {keys.Count} key(s)");
                var db = new WeChatDatabase(accountDir, Path.GetDirectoryName(extractor.CacheFile)!, keys, log);
                if (!db.CanReadCore(log))
                {
                    failures.Add($"{db.Account}: 密钥无法解开 session/contact/bizchat");
                    db.Dispose();
                    continue;
                }
                return db;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or InvalidOperationException or CryptographicException)
            {
                failures.Add($"{Path.GetFileName(accountDir)}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            "无法打开微信数据库。请确认微信已登录；如微信以管理员运行，本程序也需同权限。" +
            (failures.Count > 0 ? " " + string.Join("; ", failures) : ""));
    }

    private static string FindDb(string storage, string name) =>
        FindOptionalDb(storage, name) ?? throw new FileNotFoundException($"未找到 {name}", storage);

    private static string? FindOptionalDb(string storage, string name) =>
        Directory.EnumerateFiles(storage, name, SearchOption.AllDirectories)
            .FirstOrDefault(p => !p.Contains(Path.DirectorySeparatorChar + "migrate" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));

    private bool CanReadCore(Action<string>? log = null)
    {
        try
        {
            EnsureSnapshot(SessionDbPath);
            EnsureSnapshot(ContactDbPath);
            if (BizChatDbPath != null) EnsureSnapshot(BizChatDbPath);
            using var conn = OpenReadOnly(SessionDbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM SessionTable";
            cmd.ExecuteScalar();
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidDataException)
        {
            log?.Invoke($"core read failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public List<WeChatSession> GetSessions()
    {
        using var sessions = OpenReadOnly(SessionDbPath);
        using var contacts = OpenReadOnly(ContactDbPath);
        using var bizchat = BizChatDbPath == null ? null : OpenReadOnly(BizChatDbPath);
        return ReadSessions(sessions, contacts, bizchat);
    }

    internal static List<WeChatSession> ReadSessions(
        SqliteConnection sessions, SqliteConnection contacts, SqliteConnection? bizchat)
    {
        var result = new List<WeChatSession>();
        using var cmd = sessions.CreateCommand();
        cmd.CommandText = """
            SELECT username, unread_count, summary, last_timestamp,
                   last_msg_sender, last_sender_display_name
            FROM SessionTable WHERE is_hidden=0
            ORDER BY sort_timestamp DESC
            """;

        var contactInfo = ContactMetadata(contacts);
        var businessInfo = BusinessMetadata(bizchat, contacts, contactInfo);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var username = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim();
            if (username.Length == 0) continue;

            var summary = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var senderUsername = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var lastSender = reader.IsDBNull(5) ? "" : reader.GetString(5);
            if (string.IsNullOrWhiteSpace(lastSender) || lastSender == senderUsername)
                lastSender = businessInfo.TryGetValue(senderUsername, out var sender) ? sender.Name : senderUsername;

            (string Name, bool Muted, bool Official) info;
            var companyName = "";
            if (username.EndsWith("@qy_g", StringComparison.OrdinalIgnoreCase)
                || username.EndsWith("@qy_u", StringComparison.OrdinalIgnoreCase))
            {
                // 子会话使用自身的状态，不能继承父服务号标记或默认成未静音。
                if (!businessInfo.TryGetValue(username, out var business)
                    || string.IsNullOrWhiteSpace(business.Name) || string.IsNullOrWhiteSpace(business.CompanyName))
                    throw new InvalidDataException($"未找到企业微信会话的名称、企业名称或免打扰状态: {username}");
                info = (business.Name, business.Muted, false);
                companyName = business.CompanyName;
            }
            else if (!contactInfo.TryGetValue(username, out info))
                info = (username, false, false);

            result.Add(new WeChatSession(
                Username: username,
                Nickname: info.Name,
                Summary: summary,
                LastTimestamp: reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                LastSender: lastSender,
                Unread: reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                Muted: info.Muted,
                Official: info.Official) { CompanyName = companyName });
        }
        return result;
    }

    private static Dictionary<string, (string Name, bool Muted, bool Official)> ContactMetadata(SqliteConnection contacts)
    {
        var map = new Dictionary<string, (string, bool, bool)>(StringComparer.Ordinal);
        using var cmd = contacts.CreateCommand();
        cmd.CommandText = "SELECT username, nick_name, remark, chat_room_notify, verify_flag FROM contact";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0)) continue;
            var username = r.GetString(0);
            var remark = r.IsDBNull(2) ? "" : r.GetString(2);
            var nick = r.IsDBNull(1) ? "" : r.GetString(1);
            var name = string.IsNullOrWhiteSpace(remark) ? (string.IsNullOrWhiteSpace(nick) ? username : nick) : remark;
            var notify = r.IsDBNull(3) ? 1 : r.GetInt32(3);
            var verify = r.IsDBNull(4) ? 0 : r.GetInt32(4);
            map[username] = (name,
                username.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase) && notify == 0,
                (verify & 8) != 0);
        }
        return map;
    }

    private static Dictionary<string, (string Name, bool Muted, string CompanyName)> BusinessMetadata(
        SqliteConnection? bizchat, SqliteConnection contacts,
        IReadOnlyDictionary<string, (string Name, bool Muted, bool Official)> contactInfo)
    {
        var map = new Dictionary<string, (string, bool, string)>(StringComparer.Ordinal);
        if (bizchat == null) return map;

        // 企业消息服务号通过 belong 指向企业主体，不能直接用“企业名称消息”作前缀。
        var companies = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var parents = contacts.CreateCommand())
        {
            parents.CommandText = "SELECT username, belong FROM biz_info WHERE belong IS NOT NULL AND belong <> ''";
            using var parentReader = parents.ExecuteReader();
            while (parentReader.Read()) companies[parentReader.GetString(0)] = parentReader.GetString(1);
        }

        using var cmd = bizchat.CreateCommand();
        cmd.CommandText = """
            SELECT group_id, chat_name, bit_flag, brand_user_name FROM chat_group
            UNION ALL
            SELECT user_id, user_name, bit_flag, brand_user_name FROM user_info
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0)) continue;
            var username = r.GetString(0);
            var brand = r.IsDBNull(3) ? "" : r.GetString(3);
            var companyId = companies.GetValueOrDefault(brand, brand);
            var companyName = contactInfo.TryGetValue(companyId, out var company) && company.Name != companyId
                ? company.Name : "";
            // bizchat 的 bit 0 是子会话免打扰，不是 contact.chat_room_notify。
            map[username] = (r.IsDBNull(1) ? "" : r.GetString(1), (r.GetInt32(2) & 1) != 0, companyName);
        }
        return map;
    }

    private SqliteConnection OpenReadOnly(string plainDb)
    {
        var snapshot = EnsureSnapshot(plainDb);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = snapshot, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        conn.Open();
        return conn;
    }

    private string EnsureSnapshot(string source)
    {
        lock (_gate)
        {
            var rel = Path.GetRelativePath(Path.Combine(_accountDir, "db_storage"), source).Replace('/', '\\');
            var key = _keys.FirstOrDefault(kv =>
                string.Equals(kv.Key.Replace('/', Path.DirectorySeparatorChar), rel, StringComparison.OrdinalIgnoreCase)).Value;
            if (key == null) throw new InvalidDataException($"没有可用密钥: {rel}");

            var dst = Path.Combine(_workDir, rel.Replace(Path.DirectorySeparatorChar, '_'));
            var wal = source + "-wal";

            // Polling must see database changes. Cache only while the main file
            // and WAL are the same generation as the snapshot.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var signature = SnapshotSignature(source, wal);
                if (_snapshots.TryGetValue(source, out var ready)
                    && File.Exists(ready.Path)
                    && ready.Signature == signature)
                    return ready.Path;

                var tmp = dst + "." + Environment.ProcessId + "." + attempt + ".tmp";
                try
                {
                    SqlCipherPage.DecryptFile(source, tmp, key);
                    if (File.Exists(wal) && new FileInfo(wal).Length > 32)
                    {
                        var frames = SqlCipherPage.MergeWal(tmp, wal, key);
                        _log?.Invoke($"WAL {rel}: {frames} committed frame(s)");
                    }

                    // A writer may append while we read. Retry rather than
                    // publish a snapshot assembled from two WAL generations.
                    if (SnapshotSignature(source, wal) != signature)
                    {
                        _log?.Invoke($"snapshot {rel} changed during decrypt, retrying");
                        continue;
                    }

                    _log?.Invoke($"snapshot {rel} size={new FileInfo(tmp).Length}");
                    if (!QuickCheck(tmp))
                        throw new InvalidDataException("解密结果校验失败，微信可能正在 checkpoint");

                    if (File.Exists(dst))
                        File.Replace(tmp, dst, null);
                    else
                        File.Move(tmp, dst);
                    _snapshots[source] = new Snapshot(dst, signature);
                    return dst;
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
            }

            throw new InvalidDataException("微信数据库持续变化，无法生成一致快照");
        }
    }

    private static string SnapshotSignature(string source, string wal)
    {
        static string Part(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "-";
        }
        // Windows can keep the mtime/length unchanged while a preallocated WAL
        // is overwritten. The wal-index headers also change on each commit.
        var shm = source + "-shm";
        var walIndex = "-";
        if (File.Exists(shm))
        {
            using var stream = new FileStream(shm, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[96];
            stream.ReadExactly(header);
            walIndex = Convert.ToHexString(header);
        }
        return Part(source) + "|" + Part(wal) + "|" + walIndex;
    }

    private static bool QuickCheck(string path)
    {
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check";
            using var r = cmd.ExecuteReader();
            var ok = false;
            while (r.Read())
            {
                ok = r.GetString(0) == "ok";
                if (!ok) return false;
            }
            return ok;
        }
        catch (SqliteException) { return false; }
    }

    public void Dispose()
    {
        lock (_gate) _snapshots.Clear();
    }
}
