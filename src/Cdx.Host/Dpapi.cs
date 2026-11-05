using System.Runtime.InteropServices;

namespace Cdx;

// Chiffrement lié au compte Windows (DPAPI, crypt32) : secrets de la suite (mots de passe importés, jetons Google). Lisible par ce seul utilisateur, sur ce PC.
// `entropy` (facultative) : un sel propre à l'usage ; un autre programme de l'utilisateur qui appelle DPAPI sans elle ne peut pas déchiffrer le blob.
static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] struct DATA_BLOB { public int cbData; public IntPtr pbData; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CryptProtectData(ref DATA_BLOB i, string? descr, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB o);
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref DATA_BLOB i, IntPtr descr, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB o);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);

    public static byte[] Protect(byte[] data, string what = "CDX", byte[]? entropy = null) => Run(data, true, what, entropy);
    public static byte[] Unprotect(byte[] data, byte[]? entropy = null) => Run(data, false, null, entropy);

    static DATA_BLOB Blob(byte[] b)
    {
        var blob = new DATA_BLOB { cbData = b.Length, pbData = Marshal.AllocHGlobal(Math.Max(1, b.Length)) };
        Marshal.Copy(b, 0, blob.pbData, b.Length);
        return blob;
    }

    static byte[] Run(byte[] data, bool protect, string? what, byte[]? entropy)
    {
        var inb = Blob(data);
        var ent = entropy != null ? Blob(entropy) : default;
        IntPtr entPtr = IntPtr.Zero;
        try
        {
            if (entropy != null) { entPtr = Marshal.AllocHGlobal(Marshal.SizeOf<DATA_BLOB>()); Marshal.StructureToPtr(ent, entPtr, false); }
            bool ok = protect ? CryptProtectData(ref inb, what, entPtr, IntPtr.Zero, IntPtr.Zero, 0, out var o) : CryptUnprotectData(ref inb, IntPtr.Zero, entPtr, IntPtr.Zero, IntPtr.Zero, 0, out o);
            if (!ok) throw new InvalidOperationException("DPAPI : erreur " + Marshal.GetLastWin32Error());
            try { var r = new byte[o.cbData]; Marshal.Copy(o.pbData, r, 0, o.cbData); return r; } finally { LocalFree(o.pbData); }
        }
        finally
        {
            Marshal.FreeHGlobal(inb.pbData);
            if (entropy != null) Marshal.FreeHGlobal(ent.pbData);
            if (entPtr != IntPtr.Zero) Marshal.FreeHGlobal(entPtr);
        }
    }
}
