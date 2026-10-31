using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kdx;

// Une pseudo-console Windows (ConPTY) et le programme qui tourne dedans, en général un shell.
// Windows y joue le rôle du terminal « de l'intérieur » : il traduit l'écran de la console en séquences VT (UTF-8) qu'on lit sur `Output`, et reçoit sur `Write`
// ce que l'utilisateur tape (aussi en VT/UTF-8). Rien ici ne sait dessiner : c'est la page (xterm.js) qui affiche.
//
//   var pty = ConPty.Start("powershell.exe -NoLogo", dossier, 120, 30);
//   pty.Output += bytes => ...;      // sur un fil de fond ; l'ordre des morceaux est celui de la console
//   pty.Exited += code => ...;       // une seule fois, après le dernier morceau de sortie
//   pty.Write(Encoding.UTF8.GetBytes("dir\r"));  pty.Resize(100, 40);  pty.Dispose();
sealed class ConPty : IDisposable
{
    public event Action<byte[]> Output;
    public event Action<int> Exited;

    IntPtr hPC, hProcess, hThread;
    SafeFileHandle inWrite, outRead;
    FileStream input, output;
    readonly BlockingCollection<byte[]> toShell = new();
    volatile bool disposed;
    int closedPc;   // 1 quand ClosePseudoConsole a été appelé (une seule fois)
    public int ProcessId { get; private set; }

    ConPty() { }

    public static ConPty Start(string commandLine, string workDir, int cols, int rows, IDictionary<string, string> extraEnv = null)
    {
        var p = new ConPty();
        try { p.Launch(commandLine, workDir, cols, rows, extraEnv); }
        catch { p.Dispose(); throw; }
        return p;
    }

    void Launch(string commandLine, string workDir, int cols, int rows, IDictionary<string, string> extraEnv)
    {
        // Deux tubes : ce que nous écrivons (clavier) et ce que nous lisons (écran). Chacun a un côté pour nous et un côté pour la pseudo-console.
        if (!CreatePipe(out var inRead, out inWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out outRead, out var outWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        try
        {
            int hr = CreatePseudoConsole(new Coord(cols, rows), inRead, outWrite, 0, out hPC);
            if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole a échoué (0x" + hr.ToString("X") + ")");
        }
        finally { inRead.Dispose(); outWrite.Dispose(); }   // la pseudo-console a ses propres copies de ces côtés

        // Liste d'attributs : « ce processus s'attache à cette pseudo-console ».
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attrs = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attrs, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attrs;
            // Sans cela, si ce processus a ses propres entrées-sorties (lancé depuis un tube, un script d'essai), le programme les hérite et écrit à côté de la pseudo-console.
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            si.StartupInfo.hStdInput = si.StartupInfo.hStdOutput = si.StartupInfo.hStdError = new IntPtr(-1);

            IntPtr env = BuildEnvironment(extraEnv);
            try
            {
                var cmd = new StringBuilder(commandLine);
                if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, env, workDir, ref si, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Impossible de lancer « " + commandLine + " »");
                hProcess = pi.hProcess; hThread = pi.hThread; ProcessId = pi.dwProcessId;
            }
            finally { if (env != IntPtr.Zero) Marshal.FreeHGlobal(env); }
        }
        finally { DeleteProcThreadAttributeList(attrs); Marshal.FreeHGlobal(attrs); }

        input = new FileStream(inWrite, FileAccess.Write, 1, false);
        output = new FileStream(outRead, FileAccess.Read, 1, false);

        // 1. lecture de l'écran ; à la fin du flux (pseudo-console fermée), on annonce la sortie du programme.
        new Thread(ReadLoop) { IsBackground = true, Name = "conpty-sortie" }.Start();
        // 2. écriture au clavier, dans l'ordre, sans jamais bloquer l'interface si le shell est occupé.
        new Thread(WriteLoop) { IsBackground = true, Name = "conpty-entrée" }.Start();
        // 3. quand le programme se termine, on ferme la pseudo-console : c'est ce qui met fin au flux de lecture (une fois tout lu).
        new Thread(() =>
        {
            WaitForSingleObject(hProcess, 0xFFFFFFFF);
            ClosePc();
        }) { IsBackground = true, Name = "conpty-attente" }.Start();
    }

    void ReadLoop()
    {
        var buf = new byte[32768];
        try
        {
            while (true)
            {
                int n = output.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                var chunk = new byte[n]; Buffer.BlockCopy(buf, 0, chunk, 0, n);
                Output?.Invoke(chunk);
            }
        }
        catch (Exception) { /* tube cassé : la pseudo-console est fermée */ }
        int code = -1;
        try { if (hProcess != IntPtr.Zero) { WaitForSingleObject(hProcess, 5000); if (GetExitCodeProcess(hProcess, out uint c)) code = unchecked((int)c); } } catch (Exception) { }
        if (!disposed) Exited?.Invoke(code);
    }

    void WriteLoop()
    {
        try
        {
            foreach (var data in toShell.GetConsumingEnumerable())
            {
                input.Write(data, 0, data.Length);
                input.Flush();
            }
        }
        catch (Exception) { /* le shell est parti */ }
    }

    public void Write(byte[] data) { if (!disposed && data.Length > 0) { try { toShell.Add(data); } catch (InvalidOperationException) { } } }
    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    public void Resize(int cols, int rows)
    {
        if (disposed || hPC == IntPtr.Zero || cols < 1 || rows < 1) return;
        ResizePseudoConsole(hPC, new Coord(cols, rows));
    }

    void ClosePc()
    {
        if (Interlocked.Exchange(ref closedPc, 1) == 1) return;
        var h = hPC; if (h != IntPtr.Zero) ClosePseudoConsole(h);
    }

    // Ferme tout. Le programme encore vivant est arrêté : fermer la fenêtre d'un terminal ferme ce qui tourne dedans.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { toShell.CompleteAdding(); } catch { }
        try { ClosePc(); } catch { }
        try { if (hProcess != IntPtr.Zero && WaitForSingleObject(hProcess, 300) != 0) TerminateProcess(hProcess, 1); } catch { }
        try { input?.Dispose(); } catch { }
        try { output?.Dispose(); } catch { }
        try { inWrite?.Dispose(); outRead?.Dispose(); } catch { }
        if (hThread != IntPtr.Zero) { CloseHandle(hThread); hThread = IntPtr.Zero; }
        if (hProcess != IntPtr.Zero) { CloseHandle(hProcess); hProcess = IntPtr.Zero; }
    }

    // Le bloc d'environnement du programme : celui de ce processus, plus ce qui dit aux programmes qu'ils sont dans un terminal en couleurs vraies.
    static IntPtr BuildEnvironment(IDictionary<string, string> extra)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) vars[(string)e.Key] = (string)e.Value;
        vars["COLORTERM"] = "truecolor";
        vars["TERM_PROGRAM"] = "KDX";
        if (extra != null) foreach (var kv in extra) vars[kv.Key] = kv.Value;
        var sb = new StringBuilder();
        foreach (var kv in vars) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    // ---------- Windows ----------
    const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016, STARTF_USESTDHANDLES = 0x00000100;

    [StructLayout(LayoutKind.Sequential)] struct Coord { public short X, Y; public Coord(int x, int y) { X = (short)Math.Clamp(x, 1, short.MaxValue); Y = (short)Math.Clamp(y, 1, short.MaxValue); } }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);
    [DllImport("kernel32.dll")] static extern int CreatePseudoConsole(Coord size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);
    [DllImport("kernel32.dll")] static extern int ResizePseudoConsole(IntPtr hPC, Coord size);
    [DllImport("kernel32.dll")] static extern void ClosePseudoConsole(IntPtr hPC);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
