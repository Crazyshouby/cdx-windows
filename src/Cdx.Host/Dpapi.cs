using System.Runtime.InteropServices;

namespace Cdx;

// Chiffrement lié au compte Windows (DPAPI, crypt32) : secrets de la suite (mots de passe importés, jetons Google). Lisible par ce seul utilisateur, sur ce PC.
static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] struct DATA_BLOB { public int cbData; public IntPtr pbData; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CryptProtectData(ref DATA_BLOB i, string descr, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB o);
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref DATA_BLOB i, IntPtr descr, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB o);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);

    public static byte[] Protect(byte[] data, string what = "CDX") => Run(data, true, what);
    public static byte[] Unprotect(byte[] data) => Run(data, false, null);

    static byte[] Run(byte[] data, bool protect, string what)
    {
        var inb = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        try
        {
            Marshal.Copy(data, 0, inb.pbData, data.Length);
            bool ok = protect ? CryptProtectData(ref inb, what, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var o) : CryptUnprotectData(ref inb, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out o);
            if (!ok) throw new InvalidOperationException("DPAPI : erreur " + Marshal.GetLastWin32Error());
            try { var r = new byte[o.cbData]; Marshal.Copy(o.pbData, r, 0, o.cbData); return r; } finally { LocalFree(o.pbData); }
        }
        finally { Marshal.FreeHGlobal(inb.pbData); }
    }
}
