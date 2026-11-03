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

namespace Cdx;

// Les fenêtres ouvertes : la liste de la barre des tâches, relue par Tick, et les statistiques. Sorti de Shell.cs (audit du 3/11/2026, 4.6).
partial class Shell
{
    // ---------- fenêtres ouvertes ----------
    bool IsTaskWindow(IntPtr h)
    {
        if (!N.IsWindowVisible(h) || h == Bar.Handle || h == Start.Handle || h == Switch.Handle || h == Center.Handle || h == AudioMenu.Handle || h == BarMenu.Handle) return false;
        int ex = N.GetWindowLong(h, -20);
        bool appWindow = (ex & 0x00040000) != 0, tool = (ex & 0x80) != 0;
        if (tool && !appWindow) return false;
        if (!appWindow && N.GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return false;
        N.DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4);
        if (cloaked != 0) return false;
        string cls = N.ClassOf(h);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow") return false;
        return N.TitleOf(h).Length > 0;
    }

    void Tick()
    {
        if (Bar.Web.CoreWebView2 == null) return;
        TaskbarState.HideWindow();
        if (tray != null) { tray.EnsureTop(); if (tray.Flush()) SendTray(); }
        var fg = N.GetAncestor(N.GetForegroundWindow(), 2);
        if (fg != IntPtr.Zero && fg != Bar.Handle && fg != Start.Handle && fg != Switch.Handle && fg != Center.Handle && fg != AudioMenu.Handle && fg != BarMenu.Handle) lastFg = fg;   // « active » = la dernière fenêtre qui n'est pas du shell

        // Un bouton par application : les fenêtres d'un même programme sont réunies (count > 1, g = clé du groupe) ; la liste de ses fenêtres s'ouvre au clic.
        var items = TaskGrouping.Build(TaskWins()).Select(g =>
        {
            var r = g.Representative;
            return new
            {
                h = r.H.ToInt64().ToString(), title = g.Many ? AppNameOf(r.Exe) : r.Title, exe = r.Exe,
                active = g.Many ? g.Active : r.Active, min = g.Many ? g.Min : r.Min, count = g.Windows.Count, g = g.Many ? g.Key : (string?)null
            };
        }).ToList();
        // La liste n'est envoyée à la page que si elle a changé (ou toutes les 5 s, au cas où la page aurait été rechargée) : avant, sérialisation et
        // message deux fois par seconde même sans rien de neuf.
        string json = JsonSerializer.Serialize(new { type = "tasks", items });
        if (json != lastTasks || tick % 10 == 0) { lastTasks = json; Bar.Web.CoreWebView2.PostWebMessageAsJson(json); }
        if (++tick % 4 == 0) SendStats();
    }
    string? lastTasks;   // null : rien encore envoyé à la barre

    // Les fenêtres de la barre des tâches, dans l'ordre où elles sont apparues. Le chemin de l'exécutable d'une fenêtre ne change pas : il est lu une fois
    // (OpenProcess + QueryFullProcessImageName à chaque tour, pour chaque fenêtre, coûtaient le plus cher de ce minuteur).
    readonly Dictionary<IntPtr, string> exeOf = new();
    List<TaskWin> TaskWins()
    {
        var found = new List<IntPtr>(); var alive = new HashSet<IntPtr>();
        N.EnumWindows((h, _) => { if (IsTaskWindow(h)) { found.Add(h); alive.Add(h); } return true; }, IntPtr.Zero);
        foreach (var h in found) if (!order.ContainsKey(h)) order[h] = ++seq;
        foreach (var dead in order.Keys.Where(k => !alive.Contains(k)).ToList()) { order.Remove(dead); exeOf.Remove(dead); }
        string Exe(IntPtr h) { if (!exeOf.TryGetValue(h, out var e) || string.IsNullOrEmpty(e)) exeOf[h] = e = N.ExePathOf(h) ?? ""; return e; }
        return found.OrderBy(h => order[h]).Select(h => { bool iconic = N.IsIconic(h); return new TaskWin(h, Exe(h), N.TitleOf(h), h == lastFg && !iconic, iconic); }).ToList();
    }

    // Le nom d'une application pour son bouton groupé : la description du programme (« Google Chrome »…), gardée en mémoire.
    readonly Dictionary<string, string> appNames = new(StringComparer.OrdinalIgnoreCase);
    string AppNameOf(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return "Application";
        if (!appNames.TryGetValue(exe, out var n)) appNames[exe] = n = PinNameOf(exe);
        return n;
    }

    void SendStats()
    {
        N.GetSystemTimes(out long idle, out long kernel, out long user);
        long dIdle = idle - pIdle, dTotal = (kernel - pKernel) + (user - pUser);
        int cpu = pIdle == 0 || dTotal <= 0 ? 0 : (int)Math.Round(100.0 * (dTotal - dIdle) / dTotal);
        pIdle = idle; pKernel = kernel; pUser = user;
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        N.GlobalMemoryStatusEx(ref ms);
        string ram = ((ms.ullTotalPhys - ms.ullAvailPhys) / 1073741824.0).ToString("0.0", CultureInfo.GetCultureInfo("fr-FR"));
        Bar.Send(new { type = "stats", cpu, ram });
    }
}
