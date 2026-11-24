using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SecureMemo.Core;

// RFC 9180 Base mode, one fresh sender context per file.
internal static class Hpke
{
    private const string Library = "libcrypto-3-x64.dll";
    [StructLayout(LayoutKind.Sequential)]
    private struct Suite { public ushort Kem, Kdf, Aead; }
    private static readonly Suite Algorithms = new() { Kem = 0x20, Kdf = 1, Aead = 2 };
    private static readonly byte[] Info = "SecureMemo/v1/records"u8.ToArray();

    [DllImport(Library)] private static extern nint OSSL_HPKE_CTX_new(int mode, Suite suite, int role, nint libctx, nint properties);
    [DllImport(Library)] private static extern void OSSL_HPKE_CTX_free(nint ctx);
    [DllImport(Library)] private static extern int OSSL_HPKE_encap(nint ctx, [Out] byte[] enc, ref nuint len, byte[] pub, nuint publen, byte[] info, nuint infolen);
    [DllImport(Library)] private static extern int OSSL_HPKE_seal(nint ctx, [Out] byte[] ct, ref nuint len, byte[] aad, nuint aadlen, byte[] pt, nuint ptlen);
    [DllImport(Library)] private static extern int OSSL_HPKE_decap(nint ctx, byte[] enc, nuint len, nint key, byte[] info, nuint infolen);
    [DllImport(Library)] private static extern int OSSL_HPKE_open(nint ctx, [Out] byte[] pt, ref nuint len, byte[] aad, nuint aadlen, byte[] ct, nuint ctlen);
    [DllImport(Library)] private static extern nint EVP_PKEY_new_raw_private_key_ex(nint libctx, [MarshalAs(UnmanagedType.LPStr)] string type, nint properties, byte[] data, nuint len);
    [DllImport(Library)] private static extern void EVP_PKEY_free(nint key);

    internal static (byte[] Enc, byte[] Ciphertext) Encrypt(byte[] publicKey, byte[] aad, byte[] plaintext)
    {
        var ctx = OSSL_HPKE_CTX_new(0, Algorithms, 0, 0, 0);
        if (ctx == 0) throw new CryptographicException();
        try
        {
            byte[] enc = new byte[32], ct = new byte[plaintext.Length + 16];
            nuint encLen = 32, ctLen = (nuint)ct.Length;
            Check(OSSL_HPKE_encap(ctx, enc, ref encLen, publicKey, 32, Info, (nuint)Info.Length));
            Check(OSSL_HPKE_seal(ctx, ct, ref ctLen, aad, (nuint)aad.Length, plaintext, (nuint)plaintext.Length));
            if (encLen != 32 || ctLen != (nuint)ct.Length) throw new CryptographicException();
            return (enc, ct);
        }
        finally { OSSL_HPKE_CTX_free(ctx); }
    }

    internal static byte[] Decrypt(byte[] privateKey, byte[] aad, byte[] enc, byte[] ciphertext)
    {
        if (enc.Length != 32 || ciphertext.Length < 16) throw new CryptographicException();
        var ctx = OSSL_HPKE_CTX_new(0, Algorithms, 1, 0, 0);
        if (ctx == 0) throw new CryptographicException();
        nint key = 0;
        byte[] plaintext = new byte[ciphertext.Length - 16];
        try
        {
            key = EVP_PKEY_new_raw_private_key_ex(0, "X25519", 0, privateKey, (nuint)privateKey.Length);
            if (key == 0) throw new CryptographicException();
            Check(OSSL_HPKE_decap(ctx, enc, 32, key, Info, (nuint)Info.Length));
            nuint len = (nuint)plaintext.Length;
            Check(OSSL_HPKE_open(ctx, plaintext, ref len, aad, (nuint)aad.Length, ciphertext, (nuint)ciphertext.Length));
            if (len != (nuint)plaintext.Length) throw new CryptographicException();
            return plaintext;
        }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
        finally { EVP_PKEY_free(key); OSSL_HPKE_CTX_free(ctx); }
    }

    private static void Check(int result)
    {
        if (result != 1) throw new CryptographicException("公钥加密数据验证失败。");
    }
}
