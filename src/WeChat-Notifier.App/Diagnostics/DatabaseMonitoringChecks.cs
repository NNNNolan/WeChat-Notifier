using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.WeChat.Database;

namespace WeChat_Notifier.Diagnostics;

internal static class DatabaseMonitoringChecks
{
    internal static string? CommitState()
    {
        using var fixture = new Fixture();
        var main = SqliteCommitState.Read(fixture.SessionPath);
        fixture.Update("session", "UPDATE SessionTable SET unread_count=4, summary='未提交消息'");
        fixture.Publish("session", committed: false);
        Require(SqliteCommitState.Read(fixture.SessionPath) == main, "uncommitted WAL changed the commit state");
        fixture.Publish("session");
        var committed = SqliteCommitState.Read(fixture.SessionPath);
        Require(committed.Commit.Frames > 0 && committed != main, "committed WAL was not detected");
        var index = File.ReadAllBytes(fixture.SessionPath + "-shm");
        var header = File.ReadAllBytes(fixture.SessionPath + "-wal")[..32];
        Require(SqliteCommitState.TryReadIndex(index.AsSpan(0, 96), header, out var commit, out _)
            && commit == committed.Commit, "valid double wal-index header was rejected");
        var corrupt = (byte[])index.Clone();
        corrupt[8] ^= 1;
        Require(!SqliteCommitState.TryReadIndex(corrupt.AsSpan(0, 96), header, out _, out _),
            "mismatched header copies were accepted");
        corrupt = (byte[])index.Clone();
        corrupt[24] ^= 1;
        corrupt.AsSpan(0, 48).CopyTo(corrupt.AsSpan(48));
        Require(!SqliteCommitState.TryReadIndex(corrupt.AsSpan(0, 96), header, out _, out _),
            "corrupt wal-index checksum was accepted");
        File.WriteAllBytes(fixture.SessionPath + "-shm", corrupt);
        try { SqliteCommitState.Read(fixture.SessionPath); return "invalid SHM silently became no change"; }
        catch (DatabaseBusyException) { }
        File.WriteAllBytes(fixture.SessionPath + "-shm", index);
        fixture.AppendUncommitted();
        Require(SqliteCommitState.Read(fixture.SessionPath) == committed, "uncommitted tail advanced SHM commit");
        File.Delete(fixture.SessionPath + "-shm");
        var withoutShm = SqliteCommitState.Read(fixture.SessionPath);
        Require(withoutShm.Commit == committed.Commit, "WAL without SHM lost its last valid commit");
        var walTime = File.GetLastWriteTimeUtc(fixture.SessionPath + "-wal");
        var walLength = new FileInfo(fixture.SessionPath + "-wal").Length;
        fixture.Update("session", "UPDATE SessionTable SET summary='相同长度新提交'");
        fixture.Publish("session", preserveLengthAndTime: true);
        var overwritten = SqliteCommitState.Read(fixture.SessionPath);
        Require(new FileInfo(fixture.SessionPath + "-wal").Length == walLength
            && File.GetLastWriteTimeUtc(fixture.SessionPath + "-wal") == walTime
            && overwritten.Commit != committed.Commit, "same-size same-mtime WAL commit was missed");
        fixture.Publish("session", newGeneration: true, bigEndian: true);
        Require(SqliteCommitState.Read(fixture.SessionPath).IsNewGeneration(overwritten),
            "new WAL generation/big-endian checksum was missed");
        fixture.Checkpoint("session");
        var checkpoint = SqliteCommitState.Read(fixture.SessionPath);
        fixture.Update("session", "UPDATE SessionTable SET unread_count=5");
        fixture.Checkpoint("session");
        File.SetLastWriteTimeUtc(fixture.SessionPath, new DateTime(checkpoint.Main.Written, DateTimeKind.Utc));
        Require(SqliteCommitState.Read(fixture.SessionPath) != checkpoint,
            "same-size same-mtime main database overwrite was missed");
        return null;
    }

    internal static string? QueriesAndMetadata()
    {
        using var fixture = new Fixture();
        using var db = fixture.Open();
        var baseline = db.ReadChanges()!;
        Require(baseline.Sessions.Count == 2 && db.SessionQueries == 1
            && db.ContactQueries == 1 && db.BusinessQueries == 1, "initial metadata read was incomplete");
        var builds = db.SnapshotBuilds;
        for (var i = 0; i < 5; i++) Require(db.ReadChanges() == null, "idle check returned new sessions");
        Require(db.SessionQueries == 1 && db.ContactQueries == 1 && db.BusinessQueries == 1
            && db.SnapshotBuilds == builds, "idle check opened SQLite or rebuilt a snapshot");
        using (var second = fixture.Open())
        {
            Require(second.ReadChanges()!.Sessions.Count == baseline.Sessions.Count, "second reader could not create a private snapshot");
        }
        Require(db.ReadChanges() == null, "another reader changed/deleted this reader's snapshots");
        fixture.Update("session", "UPDATE SessionTable SET unread_count=4, summary='新消息' WHERE username='friend'");
        fixture.Publish("session", committed: false);
        Require(db.ReadChanges() == null && db.SessionQueries == 1, "uncommitted data triggered a query");
        fixture.Publish("session");
        var read = db.ReadChanges()!;
        Require(!read.RebuildBaseline && read.Sessions.Single(s => s.Username == "friend").Unread == 4
            && db.ContactQueries == 1 && db.BusinessQueries == 1, "session arrival reloaded metadata or reset baseline");
        Require(db.IncrementalWalApplies > 0 && db.SnapshotBuilds == builds,
            "session commit rebuilt the whole database instead of applying WAL pages incrementally");
        Require(db.ReadChanges() == null, "duplicate file event replayed a committed state");
        fixture.Update("contact", "UPDATE contact SET remark='新备注' WHERE username='friend'; UPDATE contact SET nick_name='新企业' WHERE username='gh_company'");
        fixture.Publish("contact");
        read = db.ReadChanges()!;
        Require(read.Sessions.Single(s => s.Username == "friend").Nickname == "新备注"
            && read.Sessions.Single(s => s.IsGroup).DisplayName == "新企业- 企业群"
            && db.ContactQueries == 2 && db.BusinessQueries == 2, "contact-only commit did not refresh names/business mapping");
        fixture.Update("bizchat", "UPDATE chat_group SET bit_flag=1 WHERE group_id='group@qy_g'");
        fixture.Publish("bizchat");
        read = db.ReadChanges()!;
        Require(read.Sessions.Single(s => s.IsGroup).Muted && db.ContactQueries == 2 && db.BusinessQueries == 3,
            "business-only commit changed contact cache or lost mute");
        fixture.Update("session", "UPDATE SessionTable SET unread_count=0 WHERE username='friend'");
        fixture.Publish("session", newGeneration: true);
        read = db.ReadChanges()!;
        Require(!read.RebuildBaseline && read.Sessions.Single(s => s.Username == "friend").Unread == 0,
            "WAL reset lost read-state or forced historical baseline");
        fixture.Checkpoint("session");
        read = db.ReadChanges()!;
        Require(read.Sessions.Single(s => s.Username == "friend").Unread == 0 && db.ReadChanges() == null,
            "checkpoint lost committed contents or caused continuous rereads");
        Require(!db.AccountSelectionChanged(), "same-account checkpoint caused a reconnect");
        fixture.AddAccount();
        Require(db.AccountSelectionChanged(), "newer account was not detected");
        Console.WriteLine("monitor queries: idle=0, contacts cached during session updates, isolated snapshots=PASS");
        return null;
    }

    internal static string? FileEvents()
    {
        using var fixture = new Fixture();
        using var monitor = new DatabaseChangeMonitor([fixture.SessionPath, fixture.ContactPath]);
        Require(monitor.Matches(fixture.SessionPath + "-wal") && monitor.Matches(fixture.ContactPath + "-shm")
            && !monitor.Matches(fixture.SessionPath + "-wal.bak"), "watcher path boundary was incorrect");
        Require(DatabaseChangeMonitor.DueAt(100, 2000) == 1100, "continuous writes can delay indefinitely");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.SessionPath)!, "other.db"), "unrelated");
        using (var timeout = new CancellationTokenSource(250))
            Require(monitor.WaitForCheck(timeout.Token, 3000) == null, "foreign database woke the reader");
        var before = Stopwatch.StartNew();
        fixture.Publish("session");
        using (var timeout = new CancellationTokenSource(3000))
        {
            var wake = monitor.WaitForCheck(timeout.Token, 3000);
            Require(wake is { Periodic: false, Error: null } && before.ElapsedMilliseconds < 2000,
                "real WAL creation did not wake the reader promptly");
        }
        var wal = fixture.SessionPath + "-wal";
        File.Move(wal, wal + ".saved");
        using (var timeout = new CancellationTokenSource(3000))
            Require(monitor.WaitForCheck(timeout.Token, 3000) is { Periodic: false },
                "renaming a tracked WAL was ignored");
        File.Move(wal + ".saved", wal);
        using (var timeout = new CancellationTokenSource(3000))
            Require(monitor.WaitForCheck(timeout.Token, 3000) is { Periodic: false },
                "renaming back into a tracked WAL was ignored");
        var failure = new InternalBufferOverflowException("regression overflow");
        monitor.SignalError(failure);
        using (var timeout = new CancellationTokenSource(1000))
            Require(monitor.WaitForCheck(timeout.Token, 3000)?.Error == failure,
                "watcher overflow was hidden");
        return null;
    }

    internal static string? SourceLifecycle()
    {
        using var fixture = new Fixture();
        var messages = new ConcurrentQueue<SessionInfo>();
        var snapshots = new ConcurrentQueue<IReadOnlyList<WeChatSession>>();
        var errors = new ConcurrentQueue<string>();
        using var signal = new AutoResetEvent(false);
        using var source = new WeChatNotificationSource(new AppConfig(), () => true, fixture.Open);
        source.SessionsUpdated += values => { snapshots.Enqueue(values); signal.Set(); };
        source.MessageArrived += message => { messages.Enqueue(message); signal.Set(); };
        source.Diagnostic += text => { if (text.Length != 0) errors.Enqueue(text); };
        void Wait(Func<bool> predicate, string failure)
        {
            var timer = Stopwatch.StartNew();
            while (!predicate() && timer.ElapsedMilliseconds < 5000) signal.WaitOne(50);
            Require(predicate(), failure + "; diagnostics=" + string.Join("; ", errors));
        }
        source.Start();
        Wait(() => snapshots.Count >= 1, "source did not initialize its baseline");
        Thread.Sleep(100);
        Require(messages.IsEmpty, "source replayed historical unread");
        var initialReads = snapshots.Count;
        Thread.Sleep(300);
        Require(snapshots.Count == initialReads, "source polled SQLite while idle");
        fixture.Update("session", "UPDATE SessionTable SET unread_count=4, summary='事件消息' WHERE username='friend'");
        fixture.Publish("session");
        Wait(() => messages.Count == 1, "file event did not deliver exactly one message");
        source.SetPaused(true);
        fixture.Update("session", "UPDATE SessionTable SET unread_count=5 WHERE username='friend'");
        fixture.Publish("session");
        Wait(() => snapshots.Any(s => s.Any(v => v.Username == "friend" && v.Unread == 5)), "paused source stopped read-state updates");
        Require(messages.Count == 1, "paused source still notified");
        var count = snapshots.Count;
        source.SetPaused(false);
        Wait(() => snapshots.Count > count, "resume did not rebuild its baseline");
        Thread.Sleep(150);
        Require(messages.Count == 1, "resume replayed paused messages");
        var latency = Stopwatch.StartNew();
        fixture.Update("session", "UPDATE SessionTable SET unread_count=6, summary='新代次消息' WHERE username='friend'");
        fixture.Publish("session", newGeneration: true);
        Wait(() => messages.Count == 2, "first message of a new WAL generation was lost");
        Console.WriteLine($"monitor file-event latency={latency.ElapsedMilliseconds}ms (local encrypted SQLite fixture)");
        count = snapshots.Count;
        source.RefreshConnection();
        Wait(() => snapshots.Count > count, "connection refresh did not establish a baseline");
        Thread.Sleep(150);
        Require(messages.Count == 2, "reconnection replayed old messages");
        var timer = Stopwatch.StartNew();
        source.Dispose();
        Require(timer.ElapsedMilliseconds < 1000, "idle source did not cancel promptly");
        return errors.IsEmpty ? null : "source emitted unexpected errors: " + string.Join("; ", errors);
    }

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_file_control")]
    private static extern int SetReservedBytes(nint connection, nint databaseName, int operation, ref int reserved);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "WeChatNotifier-events-" + Guid.NewGuid().ToString("N"));
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(48);
        private readonly Dictionary<string, uint> _versions = [];
        private readonly Dictionary<string, uint> _salts = [];
        internal string SessionPath => Source("session");
        internal string ContactPath => Source("contact");
        private string AccountDirectory => Path.Combine(_root, "account");
        private string Plain(string name) => Path.Combine(_root, "plain", name + ".db");
        private string Source(string name) => Path.Combine(AccountDirectory, "db_storage", name, name + ".db");

        internal Fixture()
        {
            Create("session", """
                CREATE TABLE SessionTable(username TEXT PRIMARY KEY, unread_count INTEGER, summary TEXT,
                    last_timestamp INTEGER, last_msg_sender TEXT, last_sender_display_name TEXT,
                    is_hidden INTEGER, sort_timestamp INTEGER);
                INSERT INTO SessionTable VALUES
                    ('friend',3,'历史消息',1793000000,'','',0,1),
                    ('group@qy_g',0,'企业摘要',1793000000,'','',0,2);
                """);
            Create("contact", """
                CREATE TABLE contact(username TEXT,nick_name TEXT,remark TEXT,chat_room_notify INTEGER,verify_flag INTEGER);
                CREATE TABLE biz_info(username TEXT,belong TEXT);
                INSERT INTO contact VALUES ('friend','好友','备注',1,0),('gh_company','企业','',1,8);
                INSERT INTO biz_info VALUES ('gh_company','');
                """);
            Create("bizchat", """
                CREATE TABLE chat_group(group_id TEXT,chat_name TEXT,bit_flag INTEGER,brand_user_name TEXT);
                CREATE TABLE user_info(user_id TEXT,user_name TEXT,bit_flag INTEGER,brand_user_name TEXT);
                INSERT INTO chat_group VALUES ('group@qy_g','企业群',0,'gh_company');
                """);
        }

        internal WeChatDatabase Open() => new(AccountDirectory, Path.Combine(_root, "snapshot"),
            new[] { "session", "contact", "bizchat" }.ToDictionary(
                name => Path.Combine(name, name + ".db"), _ => _key));

        private void Create(string name, string sql)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Plain(name))!);
            using (var conn = Connection(name))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA page_size=4096";
                cmd.ExecuteNonQuery();
                var reserved = SqlCipherPage.ReserveSize;
                Require(SetReservedBytes(conn.Handle!.DangerousGetHandle(), 0, 38, ref reserved) == 0,
                    "fixture could not create 80-byte reserved SQLite pages");
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            Checkpoint(name);
        }

        private SqliteConnection Connection(string name)
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Plain(name), Pooling = false }.ToString());
            conn.Open();
            return conn;
        }

        internal void Update(string name, string sql)
        {
            using var conn = Connection(name);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        internal void Checkpoint(string name)
        {
            var plain = File.ReadAllBytes(Plain(name));
            Require(plain[20] == 80 && plain.Length % SqlCipherPage.PageSize == 0, "fixture SQLite reserved/page size mismatch");
            using var encrypted = new MemoryStream();
            for (var offset = 0; offset < plain.Length; offset += SqlCipherPage.PageSize)
                encrypted.Write(WeChatChecks.EncryptPage(_key, plain.AsSpan(offset, SqlCipherPage.PageSize).ToArray(),
                    offset / SqlCipherPage.PageSize + 1));
            Directory.CreateDirectory(Path.GetDirectoryName(Source(name))!);
            File.WriteAllBytes(Source(name), encrypted.ToArray());
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(Source(name) + suffix)) File.Delete(Source(name) + suffix);
        }

        internal void Publish(string name, bool committed = true, bool newGeneration = false,
            bool bigEndian = false, bool preserveLengthAndTime = false)
        {
            var path = Source(name) + "-wal";
            var previousTime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            var previousLength = File.Exists(path) ? new FileInfo(path).Length : 0;
            var salt = _salts.GetValueOrDefault(name, 123u);
            if (newGeneration) salt++;
            _salts[name] = salt;
            var header = new byte[32];
            PutBig(header, 0, bigEndian ? 0x377f0683u : 0x377f0682u);
            PutBig(header, 4, 3007000);
            PutBig(header, 8, SqlCipherPage.PageSize);
            PutBig(header, 16, salt);
            PutBig(header, 20, 456);
            uint s0 = 0, s1 = 0;
            Checksum(header.AsSpan(0, 24), bigEndian, ref s0, ref s1);
            PutBig(header, 24, s0);
            PutBig(header, 28, s1);
            var plain = File.ReadAllBytes(Plain(name));
            var pages = plain.Length / SqlCipherPage.PageSize;
            using var wal = new MemoryStream();
            wal.Write(header);
            for (var page = 1; page <= pages; page++)
            {
                var frame = new byte[24];
                PutBig(frame, 0, (uint)page);
                PutBig(frame, 4, committed && page == pages ? (uint)pages : 0);
                header.AsSpan(16, 8).CopyTo(frame.AsSpan(8));
                var encrypted = WeChatChecks.EncryptPage(_key,
                    plain.AsSpan((page - 1) * SqlCipherPage.PageSize, SqlCipherPage.PageSize).ToArray(), page);
                Checksum(frame.AsSpan(0, 8), bigEndian, ref s0, ref s1);
                Checksum(encrypted, bigEndian, ref s0, ref s1);
                PutBig(frame, 16, s0);
                PutBig(frame, 20, s1);
                wal.Write(frame);
                wal.Write(encrypted);
            }
            var bytes = wal.ToArray();
            if (preserveLengthAndTime && previousLength > bytes.Length) Array.Resize(ref bytes, checked((int)previousLength));
            File.WriteAllBytes(path, bytes);
            if (preserveLengthAndTime) File.SetLastWriteTimeUtc(path, previousTime);
            var index = new byte[32768];
            PutNative(index, 0, 3007000);
            if (committed) _versions[name] = _versions.GetValueOrDefault(name) + 1;
            PutNative(index, 8, _versions.GetValueOrDefault(name));
            index[12] = 1;
            index[13] = (byte)(bigEndian ? 1 : 0);
            BinaryPrimitives.WriteUInt16LittleEndian(index.AsSpan(14), committed ? (ushort)SqlCipherPage.PageSize : (ushort)0);
            PutNative(index, 16, committed ? (uint)pages : 0);
            PutNative(index, 20, committed ? (uint)pages : 0);
            PutNative(index, 24, committed ? s0 : 0);
            PutNative(index, 28, committed ? s1 : 0);
            header.AsSpan(16, 8).CopyTo(index.AsSpan(32));
            s0 = s1 = 0;
            Checksum(index.AsSpan(0, 40), false, ref s0, ref s1);
            PutNative(index, 40, s0);
            PutNative(index, 44, s1);
            index.AsSpan(0, 48).CopyTo(index.AsSpan(48));
            File.WriteAllBytes(Source(name) + "-shm", index);
        }

        internal void AppendUncommitted()
        {
            var path = SessionPath + "-wal";
            var bytes = File.ReadAllBytes(path);
            var index = File.ReadAllBytes(SessionPath + "-shm");
            var s0 = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(24));
            var s1 = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(28));
            var header = new byte[24];
            PutBig(header, 0, 2);
            bytes.AsSpan(16, 8).CopyTo(header.AsSpan(8));
            var page = WeChatChecks.EncryptPage(_key, new byte[SqlCipherPage.PageSize], 2);
            Checksum(header.AsSpan(0, 8), false, ref s0, ref s1);
            Checksum(page, false, ref s0, ref s1);
            PutBig(header, 16, s0);
            PutBig(header, 20, s1);
            using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            file.Write(header);
            file.Write(page);
        }

        internal void AddAccount()
        {
            var path = Path.Combine(_root, "another-account", "db_storage", "session", "session.db");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(SessionPath, path);
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(SessionPath).AddMinutes(1));
        }

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.Delete(file);
            foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
                Directory.Delete(dir);
            Directory.Delete(_root);
        }
    }

    private static void PutBig(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
    private static void PutNative(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Checksum(ReadOnlySpan<byte> bytes, bool bigEndian, ref uint s0, ref uint s1)
    {
        // Independent WAL/SHM fixture checksum writer.
        for (var i = 0; i < bytes.Length; i += 8)
        {
            var a = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes[i..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[i..]);
            var b = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes[(i + 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i + 4)..]);
            s0 = unchecked(s0 + a + s1);
            s1 = unchecked(s1 + b + s0);
        }
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
