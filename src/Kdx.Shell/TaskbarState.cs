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

static class TaskbarState
{
    static string FilePath => Path.Combine(Host.DataDir, "etat-barre.txt");

    // Les messages d'état doivent partir de la fenêtre de la barre de Windows elle-même (Shell_TrayWnd), pas de la nôtre :
    // envoyés depuis une autre fenêtre, GETSTATE répond 0 et SETSTATE est ignoré (constaté sur la build 26300).
    // Le relais de la zone de notification porte lui aussi la classe « Shell_TrayWnd » : la barre de Windows se reconnaît à son processus.
    static APPBARDATA Data() => new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = TrayRelay.ExplorerTray() };

    // L'état d'origine se lit dans le registre (StuckRects3, octet 8 : bit 0 = masquage automatique, bit 1 = toujours visible) :
    // GETSTATE reflète la mémoire de l'explorateur, qu'un shell planté peut avoir laissée modifiée.
    static int StateFromRegistry()
    {
        try
        {
            var s = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3", "Settings", null) as byte[];
            if (s != null && s.Length > 8) return s[8] & 3;
        }
        catch { }
        return 2;
    }

    public static void AutoHide()
    {
        var abd = Data();
        int orig = StateFromRegistry();
        if (!File.Exists(FilePath)) File.WriteAllText(FilePath, orig.ToString());   // écrit une seule fois : un plantage ne doit pas l'écraser
        abd.lParam = (IntPtr)(orig | N.ABS_AUTOHIDE);
        N.SHAppBarMessage(N.ABM_SETSTATE, ref abd);
        Host.Log($"barre Windows : état d'origine {orig} -> masquage automatique");
    }

    // Windows 11 libère bien la zone de travail en masquage automatique, mais ne fait pas disparaître la fenêtre : on la cache nous-mêmes.
    public static void HideWindow()
    {
        var t = TrayRelay.ExplorerTray();
        if (t != IntPtr.Zero && N.IsWindowVisible(t)) N.ShowWindow(t, 0 /* SW_HIDE */);
    }

    public static void Restore()
    {
        var t = TrayRelay.ExplorerTray();
        if (t != IntPtr.Zero) N.ShowWindow(t, 5 /* SW_SHOW */);
        if (!File.Exists(FilePath)) return;
        if (!int.TryParse(File.ReadAllText(FilePath).Trim(), out int orig)) orig = 2;
        var abd = Data();
        abd.lParam = (IntPtr)orig;
        N.SHAppBarMessage(N.ABM_SETSTATE, ref abd);
        File.Delete(FilePath);
        Host.Log($"barre Windows : état d'origine {orig} rendu");
    }
}
