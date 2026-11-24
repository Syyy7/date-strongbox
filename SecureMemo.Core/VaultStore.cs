using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace SecureMemo.Core;

internal sealed class Memo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "笔记";
    public string Title { get; set; } = "";
    public string User { get; set; } = "";
    public string Site { get; set; } = "";
    public string Password { get; set; } = "";
    public string Body { get; set; } = "";
    public string Tags { get; set; } = "";
    public string? IconName { get; set; }
    public int? IconColor { get; set; }
    public byte[]? IconPng { get; set; }
    public override string ToString() => string.IsNullOrWhiteSpace(Title) ? $"未命名{Kind}" : Title;
}

internal sealed record Envelope(int Version, Guid Id, byte[] PublicKey, byte[] Salt, byte[] Nonce, byte[] Wrapped, byte[] Tag);
internal sealed record Packet(int Version, Guid Id, byte[] Enc, byte[] Ciphertext, byte[] Mac);
internal sealed record Backup(Envelope Envelope, Packet Packet);

internal sealed class Session(Envelope envelope, byte[] privateKey, byte[] macKey, List<Memo> entries) : IDisposable
{
    internal Envelope Envelope { get; set; } = envelope;
    internal byte[] PrivateKey { get; } = privateKey;
    internal byte[] MacKey { get; } = macKey;
    internal List<Memo> Entries { get; } = entries;
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(PrivateKey);
        CryptographicOperations.ZeroMemory(MacKey);
        Entries.Clear();
    }
}

internal sealed class VaultStore : IDisposable
{
    private const int Limit = 16 * 1024 * 1024;
    private readonly string path, registryPath;
    private readonly FileStream lease;
    private static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, AllowDuplicateProperties = false,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true
    };

    internal VaultStore(string directory, string registryPath)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "vault.json");
        this.registryPath = registryPath;
        lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal bool Exists => File.Exists(path);
    public void Dispose() => lease.Dispose();

    internal Session Create(string password, List<Memo>? initial = null)
    {
        if (Exists) throw new IOException("已有保险库。");
        var id = Guid.NewGuid();
        byte[] secret = RandomNumberGenerator.GetBytes(64);
        try
        {
            byte[] pub = new X25519PrivateKeyParameters(secret, 0).GeneratePublicKey().GetEncoded();
            var e = Wrap(id, pub, secret, password);
            using var registry = Registry.CurrentUser.CreateSubKey(registryPath);
            registry.SetValue(id.ToString("D"), Pack(e), RegistryValueKind.Binary);
            registry.Flush();
            Session session = Unwrap(GetEnvelope(id), password);
            try
            {
                if (initial != null) session.Entries.AddRange(initial);
                Save(session, true);
                return session;
            }
            catch { session.Dispose(); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    internal Session Open(string password)
    {
        Packet p = Decode<Packet>(Read(path, Limit));
        var session = Unwrap(GetEnvelope(p.Id), password);
        try { session.Entries.AddRange(ReadEntries(session, p)); return session; }
        catch { session.Dispose(); throw; }
    }

    private Envelope GetEnvelope(Guid id)
    {
        using var registry = Registry.CurrentUser.OpenSubKey(registryPath);
        if (registry?.GetValue(id.ToString("D")) is not byte[] bytes || bytes.Length > 4096)
            throw new InvalidDataException("此库的本机密钥缺失，请使用完整加密备份恢复。");
        return Decode<Envelope>(bytes);
    }

    private static Envelope Wrap(Guid id, byte[] publicKey, byte[] secret, string password)
    {
        var envelope = new Envelope(1, id, publicKey, RandomNumberGenerator.GetBytes(16),
            RandomNumberGenerator.GetBytes(12), new byte[64], new byte[16]);
        byte[] key = Derive(password, envelope.Salt);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(envelope.Nonce, secret, envelope.Wrapped, envelope.Tag, KeyHeader(envelope));
            return envelope;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal Envelope PreparePasswordChange(Guid id, string oldPassword, string newPassword)
    {
        ArgumentException.ThrowIfNullOrEmpty(newPassword);
        using var source = Unwrap(GetEnvelope(id), oldPassword);
        byte[] secret = [.. source.PrivateKey, .. source.MacKey];
        try
        {
            var replacement = Wrap(id, source.Envelope.PublicKey, secret, newPassword);
            using var verified = Unwrap(replacement, newPassword);
            return replacement;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    internal void CommitPasswordChange(Session session, Envelope replacement)
    {
        if (session.Envelope.Id != replacement.Id) throw new InvalidOperationException("保险库已切换。");
        using var registry = Registry.CurrentUser.OpenSubKey(registryPath, true)
            ?? throw new InvalidDataException("找不到本机密钥。");
        registry.SetValue(replacement.Id.ToString("D"), Pack(replacement), RegistryValueKind.Binary);
        session.Envelope = replacement;
    }

    private static Session Unwrap(Envelope e, string password)
    {
        if (e.Version != 1 || e.PublicKey.Length != 32 || e.Salt.Length != 16 || e.Nonce.Length != 12 ||
            e.Tag.Length != 16 || e.Wrapped.Length != 64) throw new InvalidDataException("密钥信息格式不正确。");
        byte[] key = Derive(password, e.Salt), secret = new byte[64];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(e.Nonce, e.Wrapped, e.Tag, secret, KeyHeader(e));
            byte[] pub = new X25519PrivateKeyParameters(secret, 0).GeneratePublicKey().GetEncoded();
            if (!pub.AsSpan().SequenceEqual(e.PublicKey)) throw new CryptographicException();
            return new Session(e, secret[..32], secret[32..], []);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(secret); }
    }

    // v1 fixes Argon2id v1.3, 64 MiB, three passes, four lanes.
    private static byte[] Derive(string password, byte[] salt)
    {
        var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13).WithMemoryAsKB(65536).WithIterations(3)
            .WithParallelism(4).WithSalt(salt).Build();
        byte[] input = Encoding.UTF8.GetBytes(password), key = new byte[32];
        try
        {
            var generator = new Argon2BytesGenerator();
            generator.Init(parameters);
            generator.GenerateBytes(input, key);
            return key;
        }
        finally { CryptographicOperations.ZeroMemory(input); parameters.Clear(); }
    }

    private static byte[] Frame(params byte[][] fields)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (byte[] field in fields)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, field.Length);
            stream.Write(length);
            stream.Write(field);
        }
        return stream.ToArray();
    }
    private static byte[] Header(Guid id) => Frame("SecureMemo/v1"u8.ToArray(), Encoding.ASCII.GetBytes(id.ToString("D")));
    private static byte[] KeyHeader(Envelope e) => Frame("keys/v1"u8.ToArray(), Header(e.Id), e.PublicKey, e.Salt);
    private static byte[] Sign(Session s, byte[] enc, byte[] ciphertext) =>
        HMACSHA256.HashData(s.MacKey, Frame(Header(s.Envelope.Id), enc, ciphertext));

    internal void Save(Session s, bool first = false)
    {
        byte[] plain = Pack(s.Entries);
        try
        {
            var (enc, ct) = Hpke.Encrypt(s.Envelope.PublicKey, Header(s.Envelope.Id), plain);
            byte[] bytes = Pack(new Packet(1, s.Envelope.Id, enc, ct, Sign(s, enc, ct)));
            if (bytes.Length > Limit) throw new InvalidDataException("保险库超过 16 MiB 上限，请缩短记录。");
            Commit(path, bytes, first, temp => ReadEntries(s, Decode<Packet>(Read(temp, Limit))));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static List<Memo> ReadEntries(Session s, Packet p)
    {
        if (p.Version != 1 || p.Id != s.Envelope.Id || p.Enc.Length != 32 || p.Mac.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(Sign(s, p.Enc, p.Ciphertext), p.Mac))
            throw new InvalidDataException("保险库完整性校验失败。");
        byte[] plaintext = Hpke.Decrypt(s.PrivateKey, Header(p.Id), p.Enc, p.Ciphertext);
        try
        {
            var entries = Decode<List<Memo>>(plaintext);
            if (entries.Any(e => e == null || e.Kind is not ("账号" or "笔记")) ||
                entries.Select(e => e.Id).Distinct().Count() != entries.Count)
                throw new InvalidDataException("记录格式不正确。");
            return entries;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal void Export(Session session, string output)
    {
        if (new[] { path, path + ".bak", path + ".lock" }.Contains(Path.GetFullPath(output), StringComparer.OrdinalIgnoreCase))
            throw new IOException("请选择保险库以外的备份文件。");
        var packet = Decode<Packet>(Read(path, Limit));
        ReadEntries(session, packet);
        Commit(output, Pack(new Backup(session.Envelope, packet)), !File.Exists(output),
            tmp => Decode<Backup>(Read(tmp, Limit + 8192)), keepPrevious: false);
    }

    internal Session Restore(string input, string password)
    {
        if (Exists) throw new IOException("恢复需要空的目标库。");
        return Create(password, ReadBackup(input, password));
    }

    internal static List<Memo> ReadBackup(string input, string password)
    {
        var backup = Decode<Backup>(Read(input, Limit + 8192));
        using var source = Unwrap(backup.Envelope, password);
        return ReadEntries(source, backup.Packet);
    }

    internal int Import(Session destination, List<Memo> entries)
    {
        var existing = destination.Entries.Select(entry => entry.Id).ToHashSet();
        var added = entries.Where(entry => existing.Add(entry.Id)).ToList();
        if (added.Count == 0) return 0;
        destination.Entries.AddRange(added);
        try { Save(destination); }
        catch { destination.Entries.RemoveRange(destination.Entries.Count - added.Count, added.Count); throw; }
        return added.Count;
    }

    private static byte[] Pack<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    private static T Decode<T>(byte[] value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new InvalidDataException("文件内容为空。");

    private static byte[] Read(string file, int limit)
    {
        using var stream = File.OpenRead(file);
        if (stream.Length > limit) throw new InvalidDataException("文件超过容量上限。");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void Commit(string target, byte[] bytes, bool first, Action<string> verify, bool keepPrevious = true)
    {
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            verify(temp);
            if (first) File.Move(temp, target);
            else File.Replace(temp, target, keepPrevious ? target + ".bak" : null);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
