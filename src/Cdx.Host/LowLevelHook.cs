using System.Runtime.InteropServices;

namespace Cdx;

// Un crochet global (WH_MOUSE_LL, WH_KEYBOARD_LL) posé sur un fil à lui, avec sa propre boucle de messages. Windows appelle un tel crochet sur le fil qui l'a
// installé et fait attendre CHAQUE mouvement de souris ou frappe de tout le système jusqu'à sa réponse : posé sur le fil de l'interface, le moindre travail long
// de l'appli (une mise à la corbeille de 1 Go, une liste énorme...) fait ramer la souris ou le clavier de tout Windows. Ici, le fil ne fait que répondre au crochet ;
// la procédure ne doit rien faire de long et confier tout le reste au fil de l'interface (BeginInvoke / Post).
sealed class LowLevelHook : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);

    readonly N.HookProc proc;   // gardé en champ : le ramasse-miettes ne doit pas libérer le délégué
    readonly Thread thread;
    uint threadId;
    public IntPtr Handle { get; private set; }

    // `kind` : 14 (souris) ou 13 (clavier). Rend une fois le crochet posé (ou refusé : Handle reste nul, l'erreur est dans le journal).
    public LowLevelHook(int kind, N.HookProc proc, string label)
    {
        this.proc = proc;
        using var ready = new ManualResetEventSlim();
        thread = new Thread(() =>
        {
            threadId = N.GetCurrentThreadId();
            Handle = N.SetWindowsHookEx(kind, this.proc, N.GetModuleHandle(null), 0);
            Host.Log(Handle == IntPtr.Zero ? $"{label} refusé (erreur {Marshal.GetLastWin32Error()})" : $"{label} installé (fil dédié)");
            ready.Set();
            if (Handle == IntPtr.Zero) return;
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }   // aucun message à traiter : la boucle sert seulement à ce que Windows appelle le crochet
            N.UnhookWindowsHookEx(Handle); Handle = IntPtr.Zero;
        }) { IsBackground = true, Name = label, Priority = ThreadPriority.AboveNormal };
        thread.Start();
        ready.Wait(5000);
    }

    public IntPtr Next(int code, IntPtr wParam, IntPtr lParam) => N.CallNextHookEx(Handle, code, wParam, lParam);

    public void Dispose()
    {
        if (threadId != 0 && thread.IsAlive) { PostThreadMessage(threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero); thread.Join(1000); }
    }
}
