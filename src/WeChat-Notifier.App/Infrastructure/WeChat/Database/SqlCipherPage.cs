using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace WeChat_Notifier.Infrastructure.WeChat.Database;

/// <summary>
/// Decrypts a WeChat 4.x SQLCipher database into a plain SQLite snapshot.
///
/// Page layout (wechatauto-replica, verified on 4.1.x):
///   4096-byte pages, 80-byte reserve = IV(16) + HMAC-SHA512(64)
///   page 1's first 16 bytes are salt or a plaintext SQLite header
///   AES-256-CBC, key derived by the caller
/// The WAL is merged only for frames whose salt matches the current WAL
/// header; stale generations are what corrupt a snapshot after a checkpoint.
/// </summary>
public static class SqlCipherPage
{
    public const int PageSize = 4096;
    public const int ReserveSize = 80;
    private const int WalHeaderSize = 32;
    private const int WalFrameSize = 24 + PageSize;

    public static byte[] DecryptPage(byte[] key, byte[] page, int pageNumber)
    {
        if (page.Length != PageSize) throw new ArgumentException("page must be 4096 bytes", nameof(page));

        var iv = page.AsSpan(PageSize - ReserveSize, 16).ToArray();
        var plain = new byte[PageSize];
        var cipherKey = key.Length == 48 ? key.AsSpan(0, 32).ToArray() : key;

        if (pageNumber == 1)
        {
            // Replace the salt for raw keys; key+salt values use a plaintext header.
            if (key.Length == 48) page.AsSpan(0, 16).CopyTo(plain);
            else "SQLite format 3\0"u8.CopyTo(plain);
            DecryptCbc(cipherKey, iv, page.AsSpan(16, PageSize - ReserveSize - 16), plain.AsSpan(16));
        }
        else
        {
            DecryptCbc(cipherKey, iv, page.AsSpan(0, PageSize - ReserveSize), plain);
        }

        return plain;
    }

    private static void DecryptCbc(byte[] key, byte[] iv, ReadOnlySpan<byte> cipher, Span<byte> output)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var dec = aes.CreateDecryptor();
        var input = cipher.ToArray();
        var buf = new byte[input.Length];
        var written = dec.TransformBlock(input, 0, input.Length, buf, 0);
        buf.AsSpan(0, written).CopyTo(output);
    }

    public static void DecryptFile(string source, string destination, byte[] key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var size = new FileInfo(source).Length;
        if (size % PageSize != 0) throw new InvalidDataException("数据库包含不完整页");
        var pages = size / PageSize;
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = File.Create(destination);

        var page = new byte[PageSize];
        for (var i = 1; i <= pages; i++)
        {
            input.ReadExactly(page);
            output.Write(DecryptPage(key, page, i));
        }
    }

    /// <summary>Applies only valid frames through the last commit, including overflow pages.</summary>
    public static int MergeWal(string snapshot, string walPath, byte[] key)
    {
        using var wal = new FileStream(walPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (wal.Length == 0) return 0;
        var header = new byte[WalHeaderSize];
        wal.ReadExactly(header);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (magic is not (0x377f0682 or 0x377f0683)
            || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)) != 3007000
            || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)) != PageSize)
            throw new InvalidDataException("不支持的 WAL 头");

        var bigEndian = magic == 0x377f0683;
        uint s0 = 0, s1 = 0;
        Checksum(header.AsSpan(0, 24), bigEndian, ref s0, ref s1);
        if (s0 != BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24))
            || s1 != BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28)))
            throw new InvalidDataException("WAL 头校验失败");

        var frameCount = (wal.Length - WalHeaderSize) / WalFrameSize;
        var committed = 0;
        uint databasePages = 0;
        var frameHeader = new byte[24];
        var page = new byte[PageSize];
        // Keep the latest committed version of each page; the WAL may end mid-transaction.
        var pending = new Dictionary<uint, byte[]>();
        var pages = new Dictionary<uint, byte[]>();
        for (var i = 0; i < frameCount; i++)
        {
            wal.ReadExactly(frameHeader);
            wal.ReadExactly(page);
            if (!frameHeader.AsSpan(8, 8).SequenceEqual(header.AsSpan(16, 8))) break;
            var pgno = BinaryPrimitives.ReadUInt32BigEndian(frameHeader);
            if (pgno == 0) break;
            Checksum(frameHeader.AsSpan(0, 8), bigEndian, ref s0, ref s1);
            Checksum(page, bigEndian, ref s0, ref s1);
            if (s0 != BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(16))
                || s1 != BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(20))) break;

            pending[pgno] = DecryptPage(key, page, checked((int)pgno));
            var commitSize = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(4));
            if (commitSize == 0) continue;
            foreach (var (number, plain) in pending) pages[number] = plain;
            pending.Clear();
            databasePages = commitSize;
            committed = i + 1;
        }

        if (committed == 0) return 0;
        using var output = new FileStream(snapshot, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        foreach (var (number, plain) in pages)
        {
            if (number > databasePages) continue;
            output.Position = ((long)number - 1) * PageSize;
            output.Write(plain);
        }
        output.SetLength((long)databasePages * PageSize);
        return committed;
    }

    private static void Checksum(ReadOnlySpan<byte> bytes, bool bigEndian, ref uint s0, ref uint s1)
    {
        for (var i = 0; i < bytes.Length; i += 8)
        {
            var x0 = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes[i..])
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes[i..]);
            var x1 = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes[(i + 4)..])
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i + 4)..]);
            s0 = unchecked(s0 + x0 + s1);
            s1 = unchecked(s1 + x1 + s0);
        }
    }
}
