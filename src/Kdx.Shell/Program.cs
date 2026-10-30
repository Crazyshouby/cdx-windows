using System.Diagnostics;
using System.IO.Pipes;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Kdx;

// KDX-Shell : la barre des tâches et le menu Démarrer de la charte KDX-UI, à la place de ceux de Windows.
//   kdx-shell.exe [thème]    lance le shell (rouge bleu vert violet ambre sarcelle charbon)
//   kdx-shell.exe --restore  rend à Windows l'état de sa barre des tâches (après un arrêt brutal du shell)
// Chaque page est du HTML/CSS de la charte, servi par WebView2 ; ce programme ne fait que le lien avec Windows.
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Host.Init("shell");
        if (args.Length > 0 && args[0] == "--restore") { TaskbarState.Restore(); return; }
        if (args.Length > 1 && args[0] == "--accent") { Suite.WriteAccent(args[1]); return; }
        if (args.Length > 0 && args[0] == "--selftest-sys") { SelfTest.RunSys(); return; }
        if (args.Length > 0 && args[0] == "--selftest-audio") { SelfTest.RunAudio(); return; }
        if (args.Length > 0 && args[0] == "--selftest-bus") { SelfTest.RunBus(); return; }

        if (!SingleInstance.Acquire("shell", out var mutex)) return;
        using var _ = mutex;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (s, e) => Host.Log("exception (interface) : " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => { Host.Log("exception fatale : " + e.ExceptionObject); TaskbarState.Restore(); };

        var shell = new Shell(args.Length > 0 && Suite.IsAccent(args[0]) ? args[0] : null);
        Application.Run(shell.Bar);
        shell.Cleanup();
    }
}
