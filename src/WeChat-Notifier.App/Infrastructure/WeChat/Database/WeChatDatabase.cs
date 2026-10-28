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
    private sealed record Snapshot(string Path, SqliteCommitState State);
    private readonly Dictionary<string, Snapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _log;
    private Metadata? _metadata;
    private readonly Dictionary<string, string> _accountSessions = new(StringComparer.OrdinalIgnoreCase);
    private string? _preferredAccount;
    internal long CommitChecks { get; private set; }
    internal long SessionQueries { get; private set; }
    internal long ContactQueries { get; private set; }
    internal long BusinessQueries { get; private set; }
    internal long SnapshotBuilds { get; private set; }
    internal long IncrementalWalApplies { get; private set; }
    private sealed record Metadata(CoreCommitState State,
        Dictionary<string, (string Name, bool Muted, bool Official)> Contacts,
        Dictionary<string, (string Name, bool Muted, string CompanyName)> Business);
    private readonly record struct CoreCommitState(
        SqliteCommitState Sessions, SqliteCommitState Contacts, SqliteCommitState? Business);
    internal sealed record SessionRead(List<WeChatSession> Sessions, bool RebuildBaseline);

    public string Account { get; }
    public string SessionDbPath { get; }
    public string ContactDbPath { get; }
    public string? BizChatDbPath { get; }
    public string? Wxid { get; }

    internal WeChatDatabase(string accountDir, string workDir, Dictionary<string, byte[]> keys, Action<string>? log = null)
    {
        _accountDir = accountDir;
        _workDir = Path.Combine(workDir, "snapshot-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
        _keys = keys;
        _log = log;
        Account = Path.GetFileName(accountDir.TrimEnd(Path.DirectorySeparatorChar));
        Wxid = System.Text.RegularExpressions.Regex.Replace(Account, @"_\w{4}$", "");

        var storage = Path.Combine(accountDir, "db_storage");
        SessionDbPath = FindDb(storage, "session.db");
        ContactDbPath = FindDb(storage, "contact.db");
        BizChatDbPath = FindOptionalDb(storage, "bizchat.db");
        _preferredAccount = PreferredAccount();
    }

    internal string[] DatabasePaths => BizChatDbPath == null
        ? [SessionDbPath, ContactDbPath] : [SessionDbPath, ContactDbPath, BizChatDbPath];

    internal bool AccountSelectionChanged()
    {
        var preferred = PreferredAccount();
        if (preferred == _accountDir) { _preferredAccount = preferred; return false; }
        return preferred != _preferredAccount;
    }

    private string? PreferredAccount()
    {
        var accounts = Directory.EnumerateDirectories(Path.GetDirectoryName(_accountDir)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var removed in _accountSessions.Keys.Where(path => !accounts.Contains(path)).ToArray())
            _accountSessions.Remove(removed);
        foreach (var account in accounts)
        {
            if (_accountSessions.ContainsKey(account)) continue;
            var storage = Path.Combine(account, "db_storage");
            if (Directory.Exists(storage) && FindOptionalDb(storage, "session.db") is { } path)
                _accountSessions.Add(account, path);
        }
        // 首次发现目录才定位 session.db；闲置核对不遍历整个消息库。
        return _accountSessions.OrderByDescending(pair => WeChatDbLocator.SessionActivity(pair.Value))
            .Select(pair => pair.Key).FirstOrDefault();
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
                try
                {
                    if (db.CanReadCore(log)) return db;
                }
                catch { db.Dispose(); throw; }
                failures.Add($"{db.Account}: 密钥无法解开 session/contact/bizchat");
                db.Dispose();
            }
            catch (DatabaseBusyException) { throw; }
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
            var state = ReadCommitState();
            EnsureSnapshot(ContactDbPath, state.Contacts);
            if (BizChatDbPath != null) EnsureSnapshot(BizChatDbPath, state.Business!.Value);
            using var conn = OpenReadOnly(EnsureSnapshot(SessionDbPath, state.Sessions));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM SessionTable";
            cmd.ExecuteScalar();
            return true;
        }
        catch (DatabaseBusyException) { throw; }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidDataException)
        {
            log?.Invoke($"core read failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public List<WeChatSession> GetSessions()
    {
        return ReadChanges(force: true)!.Sessions;
    }

    /// <summary>提交未变时不打开 SQLite 连接；所有查询和缓存只由监听线程执行。</summary>
    internal SessionRead? ReadChanges(bool force = false)
    {
        lock (_gate)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var state = ReadCommitState();
                    if (!force && _metadata?.State == state) return null;
                    var sessionPath = EnsureSnapshot(SessionDbPath, state.Sessions);
                    var contactPath = EnsureSnapshot(ContactDbPath, state.Contacts);
                    var businessPath = BizChatDbPath == null ? null : EnsureSnapshot(BizChatDbPath, state.Business!.Value);
                    Dictionary<string, (string Name, bool Muted, bool Official)> contactInfo;
                    if (_metadata?.State.Contacts == state.Contacts) contactInfo = _metadata.Contacts;
                    else
                    {
                        using var contacts = OpenReadOnly(contactPath);
                        contactInfo = ContactMetadata(contacts);
                        ContactQueries++;
                    }
                    Dictionary<string, (string Name, bool Muted, string CompanyName)> businessInfo;
                    if (_metadata?.State.Contacts == state.Contacts && _metadata.State.Business == state.Business)
                        businessInfo = _metadata.Business;
                    else
                    {
                        using var contacts = businessPath == null ? null : OpenReadOnly(contactPath);
                        using var business = businessPath == null ? null : OpenReadOnly(businessPath);
                        businessInfo = BusinessMetadata(business, contacts, contactInfo);
                        if (businessPath != null) BusinessQueries++;
                    }
                    using var sessions = OpenReadOnly(sessionPath);
                    var result = ReadSessions(sessions, contactInfo, businessInfo);
                    SessionQueries++;
                    if (ReadCommitState() != state) throw new DatabaseBusyException("会话读取期间数据库发生提交");
                    // 同账户 checkpoint/WAL 重建保留会话指纹，不能漏掉新代次的第一条消息。
                    // 只有账户切换/连接重建才由上层明确建立基线。
                    const bool rebuild = false;
                    _metadata = new(state, contactInfo, businessInfo);
                    return new(result, rebuild);
                }
                catch (DatabaseBusyException) when (attempt < 2) { Thread.Sleep(20); }
            }
        }
        throw new DatabaseBusyException("数据库持续变化，无法读取一致的会话快照");
    }

    private CoreCommitState ReadCommitState()
    {
        CommitChecks++;
        return new(SqliteCommitState.Read(SessionDbPath), SqliteCommitState.Read(ContactDbPath),
            BizChatDbPath == null ? null : SqliteCommitState.Read(BizChatDbPath));
    }

    internal static List<WeChatSession> ReadSessions(
        SqliteConnection sessions, SqliteConnection contacts, SqliteConnection? bizchat)
    {
        var contactInfo = ContactMetadata(contacts);
        return ReadSessions(sessions, contactInfo, BusinessMetadata(bizchat, contacts, contactInfo));
    }

    private static List<WeChatSession> ReadSessions(SqliteConnection sessions,
        IReadOnlyDictionary<string, (string Name, bool Muted, bool Official)> contactInfo,
        IReadOnlyDictionary<string, (string Name, bool Muted, string CompanyName)> businessInfo)
    {
        var result = new List<WeChatSession>();
        using var cmd = sessions.CreateCommand();
        cmd.CommandText = """
            SELECT username, unread_count, summary, last_timestamp,
                   last_msg_sender, last_sender_display_name
            FROM SessionTable WHERE is_hidden=0
            ORDER BY sort_timestamp DESC
            """;

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
        SqliteConnection? bizchat, SqliteConnection? contacts,
        IReadOnlyDictionary<string, (string Name, bool Muted, bool Official)> contactInfo)
    {
        var map = new Dictionary<string, (string, bool, string)>(StringComparer.Ordinal);
        if (bizchat == null) return map;

        // 企业消息服务号通过 belong 指向企业主体，不能直接用“企业名称消息”作前缀。
        var companies = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var parents = contacts!.CreateCommand())
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

    private static SqliteConnection OpenReadOnly(string snapshot)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(Path.GetFullPath(snapshot)).AbsoluteUri + "?immutable=1",
            Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        try { conn.Open(); return conn; }
        catch { conn.Dispose(); throw; }
    }

    private string EnsureSnapshot(string source, SqliteCommitState state)
    {
        lock (_gate)
        {
            var rel = Path.GetRelativePath(Path.Combine(_accountDir, "db_storage"), source).Replace('/', '\\');
            var key = _keys.FirstOrDefault(kv =>
                string.Equals(kv.Key.Replace('/', Path.DirectorySeparatorChar), rel, StringComparison.OrdinalIgnoreCase)).Value;
            if (key == null) throw new InvalidDataException($"没有可用密钥: {rel}");

            var dst = Path.Combine(_workDir, rel.Replace(Path.DirectorySeparatorChar, '_'));
            var wal = source + "-wal";

            if (_snapshots.TryGetValue(source, out var ready) && File.Exists(ready.Path) && ready.State == state)
                return ready.Path;
            var tmp = dst + "." + Environment.ProcessId + ".tmp";
            try
            {
                if (_snapshots.TryGetValue(source, out ready)
                    && File.Exists(ready.Path)
                    && ready.State.Main == state.Main
                    && ready.State.MainHeader == state.MainHeader
                    && (ready.State.WalGeneration == state.WalGeneration
                        || ready.State.Commit.Frames == 0 && ready.State.WalGeneration.Length == 0)
                    && state.Commit.Frames > ready.State.Commit.Frames)
                {
                    SqlCipherPage.MergeWalIncremental(ready.Path, wal, key, ready.State.Commit, state.Commit);
                    if (SqliteCommitState.Read(source) != state)
                        throw new DatabaseBusyException("数据库在增量快照期间发生提交");
                    if (!QuickCheck(ready.Path)) throw new InvalidDataException("增量快照校验失败");
                    _snapshots[source] = new Snapshot(ready.Path, state);
                    IncrementalWalApplies++;
                    _log?.Invoke($"WAL {rel}: incremental frames {ready.State.Commit.Frames}->{state.Commit.Frames}");
                    return ready.Path;
                }
                if (SqliteCommitState.Read(source) != state) throw new DatabaseBusyException("数据库在快照开始前发生提交");
                SqlCipherPage.DecryptFile(source, tmp, key);
                if (state.Commit.Frames > 0)
                {
                    var frames = SqlCipherPage.MergeWal(tmp, wal, key, state.Commit.Frames);
                    _log?.Invoke($"WAL {rel}: {frames} committed frame(s)");
                }
                if (SqliteCommitState.Read(source) != state)
                    throw new DatabaseBusyException("数据库在快照生成期间发生提交");
                if (!QuickCheck(tmp)) throw new InvalidDataException("解密结果校验失败，微信可能正在 checkpoint");
                // 私有快照同目录原子覆盖，不合并旧文件的 ACL/元数据。
                File.Move(tmp, dst, overwrite: true);
                _snapshots[source] = new Snapshot(dst, state);
                SnapshotBuilds++;
                _log?.Invoke($"snapshot {rel} size={new FileInfo(dst).Length}");
                return dst;
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }

    private static bool QuickCheck(string path)
    {
        try
        {
            using var conn = OpenReadOnly(path);
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
        lock (_gate)
        {
            _snapshots.Clear();
            _metadata = null;
            if (!Directory.Exists(_workDir)) return;
            foreach (var file in Directory.EnumerateFiles(_workDir)) File.Delete(file);
            Directory.Delete(_workDir);
        }
    }
}
