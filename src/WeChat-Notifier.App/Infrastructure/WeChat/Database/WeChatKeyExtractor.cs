using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using WeChat_Notifier.Configuration;

namespace WeChat_Notifier.Infrastructure.WeChat.Database;

/// <summary>
/// Reads WeChat's per-database SQLCipher keys from Weixin.exe memory.
///
/// Only the process that has loaded Weixin.dll is scanned. The scan reads
/// committed memory in 1 MB chunks and keeps only the addresses that matter;
/// the previous whole-region copies grew past 1.5 GB on a live client.
/// </summary>
public sealed class WeChatKeyExtractor
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint MemCommit = 0x1000;
    private const uint PageGuard = 0x100;
    private const int PageSize = 4096;
    private const int ReserveSize = 80;
    private const int ChunkSize = 1024 * 1024;

    private static readonly byte[] CipherName = Encoding.ASCII.GetBytes("com.Tencent.WCDB.Config.Cipher");
    private static readonly byte[] XorMask = Convert.FromHexString(
        "d2c7442458020000004889442450488b" +
        "450048844c2448488944254048584c24");
    private static readonly Regex HexLiteral = new(
        @"[xX]'([0-9a-fA-F]{64,192})'", RegexOptions.Compiled);

    private readonly string _accountDir;
    private readonly string _cacheFile;
    private readonly Action<string>? _log;

    public WeChatKeyExtractor(string accountDir, string? workDir = null, Action<string>? log = null)
    {
        _accountDir = accountDir;
        _log = log;
        var account = Path.GetFileName(accountDir.TrimEnd(Path.DirectorySeparatorChar));
        workDir ??= Path.Combine(Path.GetTempPath(), "wechatauto_db", account);
        _cacheFile = Path.Combine(workDir, "keys.json");
    }

    public string CacheFile => _cacheFile;

    /// <summary>Relative database path to raw key for session/contact/bizchat metadata.</summary>
    public Dictionary<string, byte[]> LoadOrExtract()
    {
        var keys = LoadCache();
        var databases = CoreDatabases();
        if (databases.Count == 0) return keys;

        foreach (var (rel, path) in databases)
        {
            if (keys.TryGetValue(rel, out var cached) && Verify(cached, path)) continue;
            _log?.Invoke($"core miss rel={rel} path={path} cached={(keys.ContainsKey(rel) ? keys[rel].Length : 0)}");
            keys.Remove(rel);
        }

        if (databases.Any(db => !keys.ContainsKey(db.Rel)))
        {
            _log?.Invoke($"scanning Weixin memory for {databases.Count} database key(s)");
            foreach (var (rel, key) in ExtractCandidates(databases))
            {
                keys[rel] = key;
                _log?.Invoke($"key found: {rel}");
            }
        }

        if (keys.Count > 0) SaveCache(keys);
        return keys;
    }

    /// <summary>The notification path only opens session/contact and available bizchat metadata.</summary>
    public IReadOnlyList<(string Rel, string Path)> CoreDatabases()
    {
        var root = Path.Combine(_accountDir, "db_storage");
        var list = new List<(string, string)>();
        if (!Directory.Exists(root)) return list;

        foreach (var path in Directory.EnumerateFiles(root, "*.db", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (!name.Equals("session.db", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("contact.db", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("bizchat.db", StringComparison.OrdinalIgnoreCase))
                continue;
            var rel = Path.GetRelativePath(root, path);
            list.Add((rel, path));
        }
        return list;
    }

    public bool Verify(byte[] key, string databasePath)
    {
        try
        {
            using var fs = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var page1 = new byte[PageSize];
            if (fs.Read(page1, 0, PageSize) != PageSize) return false;
            var ok = VerifyPage1(key, page1);
            if (!ok)
            {
                _log?.Invoke($"verify fail salt={Convert.ToHexString(page1.AsSpan(0, 16))} " +
                             $"stored={Convert.ToHexString(page1.AsSpan(PageSize - 64, 64))}");
            }
            return ok;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// SQLCipher 4 page-1 HMAC. A 32-byte key uses the file header as salt;
    /// a 48-byte value is key+salt for plaintext-header databases.
    /// </summary>
    public static bool VerifyPage1(byte[] key, byte[] page1)
    {
        if (page1.Length < PageSize || key.Length is not (32 or 48)) return false;

        byte[] encKey;
        byte[] salt;
        if (key.Length == 48)
        {
            encKey = key[..32];
            salt = key[32..48];
        }
        else
        {
            encKey = key;
            salt = page1[..16];
        }

        var macSalt = new byte[16];
        for (var i = 0; i < 16; i++) macSalt[i] = (byte)(salt[i] ^ 0x3A);
        var macKey = Rfc2898DeriveBytes.Pbkdf2(encKey, macSalt, 2, HashAlgorithmName.SHA512, 32);
        var input = new byte[PageSize - ReserveSize + 4];
        Buffer.BlockCopy(page1, 16, input, 0, PageSize - ReserveSize);
        input[^4] = 1;
        var expected = HMACSHA512.HashData(macKey, input);
        return CryptographicOperations.FixedTimeEquals(expected, page1.AsSpan(PageSize - 64, 64));
    }

    private Dictionary<string, byte[]> LoadCache()
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var account = Path.GetFileName(_accountDir.TrimEnd(Path.DirectorySeparatorChar));
        var stableDir = Environment.GetEnvironmentVariable("WECHATAUTO_KEYS_DIR");
        var stable = !string.IsNullOrWhiteSpace(stableDir)
            ? Path.Combine(stableDir, account + ".json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "wechatauto_keys", account + ".json");

        _log?.Invoke($"cache stable={stable} exists={File.Exists(stable)} temp={_cacheFile}");
        foreach (var path in new[] { _cacheFile, stable })
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                var loaded = 0;
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    try
                    {
                        keys[p.Name] = Convert.FromHexString(p.Value.GetString() ?? "");
                        loaded++;
                    }
                    catch (FormatException) { _log?.Invoke($"cache contains invalid key encoding: {p.Name}"); }
                }
                _log?.Invoke($"cache read {path}: {loaded} entries");
            }
            catch (IOException ex) { _log?.Invoke("cache read failed: " + ex.Message); }
            catch (JsonException ex) { _log?.Invoke("cache parse failed: " + ex.Message); }
        }
        return keys;
    }

    private void SaveCache(Dictionary<string, byte[]> keys)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var data = keys.ToDictionary(kv => kv.Key, kv => Convert.ToHexString(kv.Value).ToLowerInvariant());
            var tmp = _cacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data,
                AppJsonContext.Default.DictionaryStringString), Encoding.UTF8);
            File.Move(tmp, _cacheFile, overwrite: true);
        }
        catch (IOException ex) { _log?.Invoke("cache save failed: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { _log?.Invoke("cache save failed: " + ex.Message); }
    }

    private IEnumerable<(string Rel, byte[] Key)> ExtractCandidates(
        IReadOnlyList<(string Rel, string Path)> databases)
    {
        var found = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var pid in WeixinPids())
        {
            using var process = OpenForRead(pid);
            if (process == null) continue;

            var needles = FindBytes(process.Handle, CipherName);
            _log?.Invoke($"pid {pid}: {needles.Count} config marker(s)");
            if (needles.Count == 0) continue;

            var pairs = new List<byte[]>(needles.Count);
            foreach (var addr in needles)
            {
                var pair = new byte[16];
                BinaryPrimitives.WriteUInt64LittleEndian(pair, (ulong)addr);
                BinaryPrimitives.WriteUInt64LittleEndian(pair.AsSpan(8), (ulong)CipherName.Length);
                pairs.Add(pair);
            }

            var hits = FindNear(process.Handle, needles, pairs, 64L * 1024 * 1024);
            _log?.Invoke($"pid {pid}: {hits.Count} config node reference(s)");

            foreach (var (qaddr, _) in hits)
            {
                var node = Read(process.Handle, qaddr - 0x10, 0x50);
                if (node == null || node.Length < 0x40) continue;
                var marker = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(0x10));
                if (!needles.Any(n => (ulong)n == marker)) continue;
                if (BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(0x18)) != (ulong)CipherName.Length)
                    continue;

                var configPtr = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(0x28));
                _log?.Invoke($"  q=0x{qaddr:X} config=0x{configPtr:X}");
                if (configPtr < 0x10000 || configPtr >= 0x800000000000) continue;

                var obj = Read(process.Handle, (nint)(configPtr + 0x88), 0x28);
                if (obj == null || obj.Length < 0x18) continue;
                var dataPtr = BinaryPrimitives.ReadUInt64LittleEndian(obj.AsSpan(8));
                var dataLen = BinaryPrimitives.ReadUInt64LittleEndian(obj.AsSpan(16));
                if (dataLen == 0 || dataLen > 1024 || dataPtr < 0x10000 || dataPtr >= 0x800000000000)
                    continue;

                _log?.Invoke($"  data=0x{dataPtr:X} len={dataLen}");
                var blob = Read(process.Handle, (nint)dataPtr, (int)dataLen);
                if (blob == null || (ulong)blob.Length != dataLen) continue;

                var decoded = new byte[blob.Length];
                for (var i = 0; i < blob.Length; i++)
                    decoded[i] = (byte)(blob[i] ^ XorMask[i % XorMask.Length]);

                var text = Encoding.ASCII.GetString(decoded);
                foreach (Match m in HexLiteral.Matches(text))
                {
                    var run = m.Groups[1].Value.ToLowerInvariant();
                    var starts = new List<int> { 0 };
                    if (run.Length > 96)
                    {
                        for (var s = 0; s <= run.Length - 64; s += 32) starts.Add(s);
                        starts.Add(run.Length - 64);
                    }

                    foreach (var s in starts.Distinct())
                    {
                        if (s + 64 > run.Length) continue;
                        var key = Convert.FromHexString(run.Substring(s, 64));
                        if (key.Distinct().Count() < 15) continue;

                        byte[]? salt = null;
                        if (s + 96 <= run.Length)
                            salt = Convert.FromHexString(run.Substring(s + 64, 32));

                        foreach (var (rel, path) in databases)
                        {
                            if (found.ContainsKey(rel)) continue;
                            var page1 = ReadPage1(path);
                            if (page1 == null) continue;
                            if (VerifyPage1(key, page1)) found[rel] = key;
                            else if (salt != null)
                            {
                                var combined = new byte[48];
                                key.CopyTo(combined, 0);
                                salt.CopyTo(combined, 32);
                                if (VerifyPage1(combined, page1)) found[rel] = combined;
                            }
                        }

                        if (found.Count == databases.Count) return found.Select(kv => (kv.Key, kv.Value));
                    }
                }
            }
        }
        return found.Select(kv => (kv.Key, kv.Value));
    }

    private static byte[]? ReadPage1(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var page1 = new byte[PageSize];
            return fs.Read(page1, 0, PageSize) == PageSize ? page1 : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static List<nint> FindBytes(nint process, byte[] needle) =>
        FindAny(process, new List<byte[]> { needle }).Select(h => h.Address).ToList();

    /// <summary>
    /// Looks for the std::string node pointers near the marker addresses. A
    /// reference lives in the same heap neighborhood, so a 64 MB window is
    /// enough and avoids the second full-process scan that used to cost minutes.
    /// </summary>
    private static List<(nint Address, int Pattern)> FindNear(nint process,
        IReadOnlyList<nint> markers, IReadOnlyList<byte[]> patterns, long window)
    {
        var hits = new List<(nint, int)>();
        foreach (var marker in markers)
        {
            var from = Math.Max(0, (long)marker - window);
            var to = Math.Min(long.MaxValue, (long)marker + window);
            nint addr = (nint)from;
            while ((long)addr < to)
            {
                var mbi = new MemoryBasicInformation();
                var got = VirtualQueryEx(process, addr, ref mbi, (nuint)Marshal.SizeOf<MemoryBasicInformation>());
                if (got == 0) break;

                var regionStart = (long)mbi.BaseAddress;
                var regionEnd = regionStart + (long)mbi.RegionSize;
                if (mbi.State == MemCommit
                    && (mbi.Protect & 0xFF) is 0x02 or 0x04 or 0x20 or 0x40
                    && (mbi.Protect & PageGuard) == 0
                    && mbi.RegionSize > 0 && mbi.RegionSize < 0x10000000)
                {
                    var scanStart = Math.Max(regionStart, from);
                    var scanEnd = Math.Min(regionEnd, to);
                    if (scanEnd > scanStart)
                    {
                        var count = hits.Count;
                        ScanRegion(process, (nint)scanStart, (nuint)(scanEnd - scanStart),
                            patterns, 15, hits);
                        for (var i = count; i < hits.Count; i++)
                        {
                            var (h, p) = hits[i];
                            if ((long)h < regionStart || (long)h >= regionEnd) continue;
                            hits[i] = (h, p);
                        }
                    }
                }

                var next = regionEnd;
                if (next <= (long)addr) break;
                addr = (nint)next;
            }
        }
        return hits;
    }
    private static List<(nint Address, int Pattern)> FindAny(nint process, IReadOnlyList<byte[]> patterns)
    {
        var hits = new List<(nint, int)>();
        nint addr = 0;
        var maxPattern = patterns.Max(p => p.Length);
        var overlap = Math.Max(0, maxPattern - 1);

        while (true)
        {
            var mbi = new MemoryBasicInformation();
            var got = VirtualQueryEx(process, addr, ref mbi, (nuint)Marshal.SizeOf<MemoryBasicInformation>());
            if (got == 0) break;

            if (mbi.State == MemCommit
                && (mbi.Protect & 0xFF) is 0x02 or 0x04 or 0x20 or 0x40
                && (mbi.Protect & PageGuard) == 0
                && mbi.RegionSize > 0 && mbi.RegionSize < 0x10000000)
            {
                ScanRegion(process, mbi.BaseAddress, mbi.RegionSize, patterns, overlap, hits);
            }

            var next = mbi.BaseAddress + (nint)mbi.RegionSize;
            if (next <= addr) break;
            addr = next;
        }
        return hits;
    }

    internal static void ScanRegion(nint process, nint start, nuint size,
        IReadOnlyList<byte[]> patterns, int overlap, List<(nint, int)> hits)
    {
        var carry = Array.Empty<byte>();
        for (nuint offset = 0; offset < size; offset += ChunkSize)
        {
            var want = (int)Math.Min((nuint)ChunkSize, size - offset);
            var chunk = Read(process, start + (nint)offset, want);
            if (chunk == null)
            {
                carry = Array.Empty<byte>();
                continue;
            }

            var data = carry.Length == 0 ? chunk : carry.Concat(chunk).ToArray();
            var dataAddress = start + (nint)offset - carry.Length;
            for (var p = 0; p < patterns.Count; p++)
            {
                var pos = 0;
                while (pos < data.Length)
                {
                    var match = data.AsSpan(pos).IndexOf(patterns[p]);
                    if (match < 0) break;
                    pos += match;
                    hits.Add((dataAddress + pos, p));
                    pos++;
                }
            }

            carry = data.Length > overlap ? data[^overlap..] : data;
        }
    }

    private static byte[]? Read(nint process, nint address, int size)
    {
        if (size <= 0) return null;
        var buf = new byte[size];
        return ReadProcessMemory(process, address, buf, (nuint)size, out var read) && read > 0
            ? buf[..(int)read]
            : null;
    }

    private sealed class ProcessHandle : IDisposable
    {
        public nint Handle { get; }
        public ProcessHandle(nint handle) => Handle = handle;
        public void Dispose() => CloseHandle(Handle);
    }

    private static ProcessHandle? OpenForRead(int pid)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, (uint)pid);
        return handle == nint.Zero ? null : new ProcessHandle(handle);
    }

    private static List<int> WeixinPids()
    {
        var pids = new List<int>();
        foreach (var name in new[] { "Weixin", "WeChat" })
        {
            Process[] all;
            try { all = Process.GetProcessesByName(name); }
            catch (InvalidOperationException) { continue; }
            foreach (var p in all)
            {
                try
                {
                    if (p.Modules.Cast<ProcessModule>()
                        .Any(m => m.ModuleName.Equals("Weixin.dll", StringComparison.OrdinalIgnoreCase)))
                        pids.Add(p.Id);
                }
                catch (System.ComponentModel.Win32Exception) { }
                finally { p.Dispose(); }
            }
        }
        return pids;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(nint process, nint address,
        ref MemoryBasicInformation info, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint process, nint address,
        byte[] buffer, nuint size, out nuint read);
}
