using System.Buffers.Binary;

namespace WeChat_Notifier.Infrastructure.WeChat.Database;

internal sealed class DatabaseBusyException(string message) : IOException(message);

internal readonly record struct DatabaseFileStamp(long Length, long Written, long Created);

/// <summary>只比较已提交状态；未提交的 WAL 长度变化不属于新版本。</summary>
internal readonly record struct SqliteCommitState(
    DatabaseFileStamp Main, string MainHeader, string WalGeneration, SqlCipherPage.WalCommit Commit, uint Change)
{
    internal string MainSalt => MainHeader[..32];
    internal bool IsNewGeneration(SqliteCommitState previous) =>
        Main.Created != previous.Main.Created || MainSalt != previous.MainSalt
        || WalGeneration != previous.WalGeneration || Commit.Frames < previous.Commit.Frames;

    internal static SqliteCommitState Read(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var before = Stamp(path);
                if (before.Length < SqlCipherPage.PageSize) throw new DatabaseBusyException("数据库头尚未完整写入");
                using var main = Open(path);
                var mainHeader = new byte[64];
                main.ReadExactly(mainHeader);
                var walPath = path + "-wal";
                var walStamp = OptionalStamp(walPath);
                if (walStamp == null || walStamp.Value.Length == 0)
                {
                    if (Stamp(path) == before && OptionalStamp(walPath) == walStamp && MainHeaderUnchanged(main, mainHeader))
                        return new(before, Convert.ToHexString(mainHeader), "", default, 0);
                    continue;
                }
                if (walStamp.Value.Length < SqlCipherPage.WalHeaderSize) continue;
                using var wal = Open(walPath);
                var header = new byte[SqlCipherPage.WalHeaderSize];
                wal.ReadExactly(header);
                SqlCipherPage.ValidateWalHeader(header);
                SqlCipherPage.WalCommit commit;
                uint change = 0;
                var shmPath = path + "-shm";
                if (File.Exists(shmPath))
                {
                    using var shm = Open(shmPath);
                    Span<byte> index = stackalloc byte[96];
                    if (!ReadIndex(shm, index) || !TryReadIndex(index, header, out commit, out change)) continue;
                    if (commit.Frames > 0)
                    {
                        var end = SqlCipherPage.WalHeaderSize + (long)commit.Frames * SqlCipherPage.WalFrameSize;
                        if (wal.Length < end) continue;
                        wal.Position = end - SqlCipherPage.WalFrameSize;
                        Span<byte> frame = stackalloc byte[24];
                        wal.ReadExactly(frame);
                        if (!frame[8..16].SequenceEqual(header.AsSpan(16, 8))
                            || BinaryPrimitives.ReadUInt32BigEndian(frame[4..]) != commit.DatabasePages
                            || BinaryPrimitives.ReadUInt32BigEndian(frame[16..]) != commit.Checksum0
                            || BinaryPrimitives.ReadUInt32BigEndian(frame[20..]) != commit.Checksum1) continue;
                    }
                    Span<byte> after = stackalloc byte[96];
                    if (!ReadIndex(shm, after) || !after.SequenceEqual(index)) continue;
                }
                else
                {
                    // 关闭后的数据库可能只有 WAL 没有 SHM；校验帧但不解密，不用 mtime 猜测提交。
                    commit = SqlCipherPage.ReadCommit(wal);
                }
                wal.Position = 0;
                Span<byte> afterHeader = stackalloc byte[32];
                wal.ReadExactly(afterHeader);
                if (!afterHeader.SequenceEqual(header) || Stamp(path) != before || !MainHeaderUnchanged(main, mainHeader)) continue;
                return commit.Frames == 0
                    ? new(before, Convert.ToHexString(mainHeader), "", default, 0)
                    : new(before, Convert.ToHexString(mainHeader), Convert.ToHexString(header.AsSpan(16, 8)), commit, change);
            }
            catch (Exception ex) when (ex is EndOfStreamException or FileNotFoundException)
            {
                if (attempt == 2)
                {
                    if (!File.Exists(path)) throw new FileNotFoundException("微信数据库已删除或移动", path, ex);
                    throw new DatabaseBusyException("数据库/WAL 在校验期间被替换：" + ex.Message);
                }
            }
            if (attempt < 2) Thread.Sleep(10);
        }
        throw new DatabaseBusyException("数据库提交头正在变化或两份 SHM 头不一致，稍后重试");
    }

    internal static bool TryReadIndex(ReadOnlySpan<byte> index, ReadOnlySpan<byte> walHeader,
        out SqlCipherPage.WalCommit commit, out uint change)
    {
        commit = default;
        change = 0;
        if (index.Length != 96 || walHeader.Length != 32 || !index[..48].SequenceEqual(index[48..])
            || ReadNative(index) != 3007000 || ReadNative(index[4..]) != 0 || index[12] != 1
            || index[13] != (SqlCipherPage.ValidateWalHeader(walHeader) ? 1 : 0)
            || (ReadNative(index[16..]) > 0
                ? ReadNative16(index[14..]) != SqlCipherPage.PageSize
                : ReadNative16(index[14..]) is not (0 or SqlCipherPage.PageSize))
            || !index[32..40].SequenceEqual(walHeader[16..24])) return false;
        uint s0 = 0, s1 = 0;
        SqlCipherPage.Checksum(index[..40], !BitConverter.IsLittleEndian, ref s0, ref s1);
        if (s0 != ReadNative(index[40..]) || s1 != ReadNative(index[44..])) return false;
        commit = new(ReadNative(index[16..]), ReadNative(index[20..]),
            ReadNative(index[24..]), ReadNative(index[28..]));
        change = ReadNative(index[8..]);
        return true;
    }

    private static bool ReadIndex(FileStream shm, Span<byte> bytes)
    {
        shm.Position = 0;
        if (shm.Length < bytes.Length) return false;
        shm.ReadExactly(bytes);
        return true;
    }

    private static bool MainHeaderUnchanged(FileStream main, byte[] before)
    {
        Span<byte> after = stackalloc byte[64];
        main.Position = 0;
        main.ReadExactly(after);
        return after.SequenceEqual(before);
    }

    private static uint ReadNative(ReadOnlySpan<byte> bytes) => BitConverter.IsLittleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
    private static ushort ReadNative16(ReadOnlySpan<byte> bytes) => BitConverter.IsLittleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1, FileOptions.RandomAccess);
    private static DatabaseFileStamp Stamp(string path)
    {
        var info = new FileInfo(path);
        return new(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
    }
    private static DatabaseFileStamp? OptionalStamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks) : null;
    }
}
