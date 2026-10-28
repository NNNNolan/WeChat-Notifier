using Microsoft.Data.Sqlite;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WeChat_Notifier.Application.Notifications;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.WeChat.Database;

namespace WeChat_Notifier.Diagnostics;

internal static class WeChatChecks
{
    public static string? BusinessSessionMetadata()
    {
        using var sessions = new SqliteConnection("Data Source=:memory:");
        using var contacts = new SqliteConnection("Data Source=:memory:");
        using var bizchat = new SqliteConnection("Data Source=:memory:");
        sessions.Open();
        contacts.Open();
        bizchat.Open();
        static void Execute(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        Execute(sessions, """
            CREATE TABLE SessionTable(
                username TEXT PRIMARY KEY, unread_count INTEGER, summary TEXT, last_timestamp INTEGER,
                last_msg_sender TEXT, last_sender_display_name TEXT, is_hidden INTEGER, sort_timestamp INTEGER);
            INSERT INTO SessionTable VALUES
                ('friend', 1, '正文', 1793000000, '', '', 0, 1),
                ('ordinary@chatroom', 1, '正文', 1793000000, '', '', 0, 2),
                ('gh_brand', 1, '正文', 1793000000, '', '', 0, 3),
                ('muted@qy_g', 1, '正文', 1793000000, 'sender@qy_u', '', 0, 4),
                ('active@qy_g', 1, '正文', 1793000000, 'sender@qy_u', '', 0, 5),
                ('muted@qy_u', 1, '正文', 1793000000, '', '', 0, 6),
                ('active@qy_u', 1, '正文', 1793000000, '', '', 0, 7);
            """);
        Execute(contacts, """
            CREATE TABLE contact(
                username TEXT, nick_name TEXT, remark TEXT, chat_room_notify INTEGER, verify_flag INTEGER);
            CREATE TABLE biz_info(username TEXT, belong TEXT);
            INSERT INTO contact VALUES
                ('friend', '好友', '备注好友', 1, 0),
                ('ordinary@chatroom', '普通群', '', 0, 0),
                ('gh_brand', '普华科技消息', '', 0, 8),
                ('gh_company', '普华科技', '', 0, 8);
            INSERT INTO biz_info VALUES ('gh_brand', 'gh_company'), ('gh_company', '');
            """);
        Execute(bizchat, """
            CREATE TABLE chat_group(group_id TEXT, brand_user_name TEXT, chat_name TEXT, bit_flag INTEGER);
            CREATE TABLE user_info(user_id TEXT, brand_user_name TEXT, user_name TEXT, bit_flag INTEGER);
            INSERT INTO chat_group VALUES
                ('muted@qy_g', 'gh_brand', '企业回复群', 1),
                ('active@qy_g', 'gh_brand', '活跃企业群', 2);
            INSERT INTO user_info VALUES
                ('muted@qy_u', 'gh_brand', '免打扰企业联系人', 1),
                ('active@qy_u', 'gh_brand', '企业联系人', 0),
                ('sender@qy_u', 'gh_brand', '小李', 0);
            """);

        var rows = WeChatDatabase.ReadSessions(sessions, contacts, bizchat);
        var byId = rows.ToDictionary(s => s.Username);
        Require(byId["muted@qy_g"].Nickname == "企业回复群", "enterprise group title fell back to its raw ID");
        Require(byId["muted@qy_g"].CompanyName == "普华科技"
            && byId["muted@qy_g"].DisplayName == "普华科技- 企业回复群",
            "enterprise title used the message service instead of its parent company");
        Require(byId["muted@qy_g"].IsGroup && byId["muted@qy_g"].Muted,
            "enterprise group mute flag was lost");
        Require(byId["muted@qy_u"].Nickname == "免打扰企业联系人" && byId["muted@qy_u"].Muted,
            "enterprise user metadata was lost");
        Require(byId["active@qy_u"].DisplayName == "普华科技- 企业联系人",
            "enterprise user title lost its company");
        Require(!byId["active@qy_g"].Muted && !byId["active@qy_g"].Official,
            "unrelated business bits or parent official flags muted an active child");
        Require(byId["friend"].Nickname == "备注好友" && byId["ordinary@chatroom"].Muted,
            "ordinary contact metadata changed");
        Require(byId["friend"].DisplayName == "备注好友" && byId["ordinary@chatroom"].DisplayName == "普通群",
            "ordinary session titles gained an enterprise prefix");
        Require(byId["active@qy_g"].LastSender == "小李", "enterprise sender name was not resolved");

        List<SessionInfo> Notify(AppConfig cfg)
        {
            using var source = new WeChatNotificationSource(cfg);
            var messages = new List<SessionInfo>();
            source.MessageArrived += messages.Add;
            source.ProcessSessions(rows);
            source.ProcessSessions(rows.Select(s => s with { Unread = s.Unread + 1 }).ToList());
            return messages;
        }
        var messages = Notify(new AppConfig());
        Require(messages.Select(s => s.Key).ToHashSet().SetEquals(["friend", "active@qy_g", "active@qy_u"]),
            "enterprise mute policy failed or all enterprise sessions were suppressed");
        Require(messages.Single(s => s.Key == "active@qy_g").Preview == "小李: 正文",
            "enterprise group preview lost its sender");
        Require(messages.Single(s => s.Key == "active@qy_g").Title == "普华科技- 活跃企业群",
            "notification title lost the enterprise prefix");
        messages = Notify(new AppConfig { SkipMuted = false });
        Require(messages.Count == 6 && messages.Single(s => s.Key == "muted@qy_g").Muted,
            "include-muted option did not apply to enterprise child sessions");
        messages = Notify(new AppConfig { SkipMuted = false, SkipSessions = ["企业回复群", "ACTIVE@QY_U"] });
        Require(messages.Count == 4 && messages.All(s => s.Key is not ("muted@qy_g" or "active@qy_u")),
            "enterprise child title or ID filter was bypassed");
        messages = Notify(new AppConfig { SkipSessions = ["普华科技- 活跃企业群"] });
        Require(messages.Count == 2 && messages.All(s => s.Key != "active@qy_g"),
            "combined enterprise title filter was bypassed");
        messages = Notify(new AppConfig { SkipKeywords = ["活跃"] });
        Require(messages.Count == 2 && messages.All(s => s.Key != "active@qy_g"),
            "enterprise child keyword filter was bypassed");
        messages = Notify(new AppConfig { SkipKeywords = ["普华"] });
        Require(messages.Count == 1 && messages[0].Key == "friend", "company keyword filter was bypassed");

        Execute(bizchat, "UPDATE chat_group SET brand_user_name='gh_company' WHERE group_id='active@qy_g'");
        Require(WeChatDatabase.ReadSessions(sessions, contacts, bizchat)
            .Single(s => s.Username == "active@qy_g").DisplayName == "普华科技- 活跃企业群",
            "direct company association lost its display name");

        void CheckMissingMetadata(SqliteConnection? business, string label)
        {
            try { WeChatDatabase.ReadSessions(sessions, contacts, business); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException(label);
        }
        CheckMissingMetadata(null, "missing bizchat database silently unmuted enterprise sessions");
        Execute(contacts, "UPDATE contact SET nick_name='' WHERE username='gh_company'");
        CheckMissingMetadata(bizchat, "missing company name silently displayed a raw brand ID");
        Execute(contacts, "UPDATE contact SET nick_name='普华科技' WHERE username='gh_company'");
        Execute(bizchat, "DELETE FROM chat_group WHERE group_id='muted@qy_g'");
        CheckMissingMetadata(bizchat, "missing enterprise child metadata silently unmuted the session");
        Execute(sessions, "DELETE FROM SessionTable WHERE username LIKE '%@qy_%'");
        Require(WeChatDatabase.ReadSessions(sessions, contacts, null).Count == 3,
            "ordinary sessions required a bizchat database");
        return null;
    }

    public static string? NotificationPolicy()
    {
        var cfg = new AppConfig();
        using var source = new WeChatNotificationSource(cfg);
        var messages = new List<SessionInfo>();
        var snapshots = new List<IReadOnlyList<WeChatSession>>();
        source.MessageArrived += messages.Add;
        source.SessionsUpdated += snapshots.Add;
        var row = new WeChatSession("friend", "好友", "历史消息", 1793000000, "", 3, false, false);
        void Read(params WeChatSession[] values) => source.ProcessSessions(values.ToList());
        void CheckCount(int expected, string label) => Require(messages.Count == expected, label);

        Read(row);
        CheckCount(0, "baseline replayed historical unread");
        Require(snapshots.Count == 1 && snapshots[0].SequenceEqual([row]), "baseline snapshot was not emitted");
        row = row with { Unread = 4, Summary = "新消息" };
        Read(row);
        Read(row);
        CheckCount(1, "arrival was missed or replayed");
        Require(snapshots.Count == 3 && snapshots[^1].SequenceEqual([row]), "no-change snapshot was not emitted");
        Require(messages[0].Unread == 4 && messages[0].Preview == "新消息", "message fields lost");
        Read(row with { Summary = "我发出的消息" });
        Read(row with { Unread = 0 });
        CheckCount(1, "own message or read transition notified");
        source.SetPaused(true);
        Read(row with { Unread = 5 });
        Require(snapshots[^1].Single().Unread == 5, "paused snapshot was not emitted");
        source.SetPaused(false);
        Read(row with { Unread = 6 });
        Require(snapshots[^1].Single().Unread == 6, "resume baseline snapshot was not emitted");
        CheckCount(1, "pause/resume replayed unread");
        source.Nudge();
        Read(row with { Unread = 7 });
        Require(snapshots[^1].Single().Unread == 7, "config baseline snapshot was not emitted");
        CheckCount(1, "config nudge replayed unread");

        Read(row with { Username = "muted@chatroom", Muted = true });
        Read(row with { Username = "official", Official = true });
        Read(row with { Username = "gh_ignored" });
        Read(row with { Username = "weixin" });
        Read(row with { Username = "system", Nickname = "微信支付" });
        cfg.SkipSessions = ["FRIEND-BY-ID", "跳过昵称"];
        cfg.SkipKeywords = ["推广"];
        Read(row with { Username = "friend-by-id" });
        Read(row with { Username = "name", Nickname = "跳过昵称" });
        Read(row with { Username = "keyword", Nickname = "推广群" });
        CheckCount(1, "muted/official/system/user filter was bypassed");

        Read();
        Require(snapshots[^1].Count == 0, "empty session snapshot was lost");
        Read(row, row with { Username = "partial", Unread = 0 });
        Require(snapshots[^1].Count == 2 && snapshots[^1].Single(s => s.Username == "partial").Unread == 0,
            "partial read-session snapshot was lost");

        cfg.SkipMuted = false;
        Read(row with { Username = "muted@chatroom", Muted = true, Unread = 5, LastSender = "小明" });
        CheckCount(2, "include muted did not take effect");
        Require(messages[^1].Muted && messages[^1].Preview == "小明: 新消息", "group metadata lost");
        cfg.IncludeOfficialAccounts = true;
        Read(row with { Username = "official", Official = true, Unread = 5 });
        CheckCount(3, "include official did not take effect");
        cfg.SkipOwnMessages = false;
        Read(row with { Username = "official", Official = true, Unread = 5, Summary = "正文更新" });
        CheckCount(4, "include own did not take effect");
        return null;
    }

    public static string? MemoryScan()
    {
        var data = new byte[1024 * 1024 + 64];
        byte[] needle = [71, 28, 93, 44];
        int[] offsets = [24, 63, 1024 * 1024 - 2, data.Length - needle.Length];
        foreach (var offset in offsets) needle.CopyTo(data, offset);
        var address = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, address, data.Length);
            using var process = Process.GetCurrentProcess();
            var hits = new List<(nint Address, int Pattern)>();
            WeChatKeyExtractor.ScanRegion(process.Handle, address, (nuint)data.Length, [needle], 3, hits);
            Require(hits.Select(h => (int)(h.Address - address)).SequenceEqual(offsets),
                "memory scanner lost offsets or chunk-boundary matches");
        }
        finally { Marshal.FreeHGlobal(address); }
        return null;
    }

    public static string? WalTransactions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WeChat_Notifier-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var snapshot = Path.Combine(dir, "snapshot.db");
        var walPath = Path.Combine(dir, "test.wal");
        try
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var original = new byte[3 * SqlCipherPage.PageSize];
            byte[] Page(byte marker) => Enumerable.Repeat(marker, SqlCipherPage.PageSize).ToArray();
            var overflow = Page(0x7f); // Overflow pages do not have a b-tree type byte.
            var committedPage = Page(0x0d);
            using var wal = new MemoryStream();
            var header = new byte[32];
            Put(header, 0, 0x377f0682);
            Put(header, 4, 3007000);
            Put(header, 8, SqlCipherPage.PageSize);
            Put(header, 16, 123);
            Put(header, 20, 456);
            uint s0 = 0, s1 = 0;
            void Checksum(byte[] bytes)
            {
                // Independent little-endian fixture writer; production also supports big-endian WALs.
                for (var i = 0; i < bytes.Length; i += 8)
                {
                    s0 = unchecked(s0 + BitConverter.ToUInt32(bytes, i) + s1);
                    s1 = unchecked(s1 + BitConverter.ToUInt32(bytes, i + 4) + s0);
                }
            }
            Checksum(header[..24]);
            Put(header, 24, s0);
            Put(header, 28, s1);
            wal.Write(header);
            void Frame(uint number, uint commitSize, byte[] plain)
            {
                var frame = new byte[24];
                Put(frame, 0, number);
                Put(frame, 4, commitSize);
                header.AsSpan(16, 8).CopyTo(frame.AsSpan(8));
                var encrypted = EncryptPage(key, plain, (int)number);
                Checksum(frame[..8]);
                Checksum(encrypted);
                Put(frame, 16, s0);
                Put(frame, 20, s1);
                wal.Write(frame);
                wal.Write(encrypted);
            }
            Frame(2, 0, overflow);
            Frame(3, 3, committedPage);
            Frame(2, 0, Page(0x02)); // Uncommitted overwrite must never become visible.
            var bytes = wal.ToArray();
            void Merge(byte[] input, int expected)
            {
                File.WriteAllBytes(snapshot, original);
                File.WriteAllBytes(walPath, input);
                Require(SqlCipherPage.MergeWal(snapshot, walPath, key) == expected, "wrong committed frame count");
                var result = File.ReadAllBytes(snapshot);
                Require(result.AsSpan(SqlCipherPage.PageSize, SqlCipherPage.PageSize - 80)
                    .SequenceEqual(overflow.AsSpan(0, SqlCipherPage.PageSize - 80)), "overflow page was lost/overwritten");
                Require(result.AsSpan(2 * SqlCipherPage.PageSize, SqlCipherPage.PageSize - 80)
                    .SequenceEqual(committedPage.AsSpan(0, SqlCipherPage.PageSize - 80)), "commit page was lost");
            }
            Merge(bytes, 2);
            Merge(bytes[..^100], 2); // Incomplete final frame.
            var stale = (byte[])bytes.Clone();
            stale[32 + 2 * (24 + SqlCipherPage.PageSize) + 8] ^= 1;
            Merge(stale, 2);
            var corrupt = (byte[])bytes.Clone();
            corrupt[^100] ^= 1;
            Merge(corrupt, 2);

            // Page 1 must round-trip and authenticate with both raw and key+salt forms.
            var page1 = Page(0);
            "SQLite format 3\0"u8.CopyTo(page1);
            foreach (var pageKey in new[] { key, key.Concat(RandomNumberGenerator.GetBytes(16)).ToArray() })
            {
                var encrypted = EncryptPage(pageKey, page1, 1);
                var salt = pageKey.Length == 48 ? pageKey[32..] : encrypted[..16];
                var macKey = Rfc2898DeriveBytes.Pbkdf2(pageKey[..32], salt.Select(b => (byte)(b ^ 0x3a)).ToArray(),
                    2, HashAlgorithmName.SHA512, 32);
                var macInput = encrypted[16..^64].Concat(new byte[] { 1, 0, 0, 0 }).ToArray();
                HMACSHA512.HashData(macKey, macInput).CopyTo(encrypted, SqlCipherPage.PageSize - 64);
                Require(WeChatKeyExtractor.VerifyPage1(pageKey, encrypted), "page 1 HMAC rejected valid key");
                encrypted[^1] ^= 1;
                Require(!WeChatKeyExtractor.VerifyPage1(pageKey, encrypted), "page 1 HMAC accepted corruption");
                Require(SqlCipherPage.DecryptPage(pageKey, encrypted, 1).AsSpan(0, SqlCipherPage.PageSize - 80)
                    .SequenceEqual(page1.AsSpan(0, SqlCipherPage.PageSize - 80)), "page 1 decrypt failed");
            }
            return null;
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(dir)) File.Delete(file);
            Directory.Delete(dir);
        }
    }

    private static byte[] EncryptPage(byte[] key, byte[] plain, int number)
    {
        var result = new byte[SqlCipherPage.PageSize];
        var iv = RandomNumberGenerator.GetBytes(16);
        var offset = number == 1 ? 16 : 0;
        if (number == 1) plain.AsSpan(0, 16).CopyTo(result);
        using var aes = Aes.Create();
        aes.Key = key[..32];
        aes.EncryptCbc(plain.AsSpan(offset, SqlCipherPage.PageSize - 80 - offset), iv, PaddingMode.None)
            .CopyTo(result, offset);
        iv.CopyTo(result, SqlCipherPage.PageSize - 80);
        return result;
    }

    private static void Put(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
