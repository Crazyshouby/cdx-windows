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

// Le crochet clavier (touche Windows, Win+E, Alt+Tab) et le sélecteur de fenêtres. Sorti de Shell.cs (audit du 3/11/2026, 4.6).
partial class Shell
{
    // ---------- crochet clavier : touche Windows, Win+E, Alt+Tab ----------
    // Windows ouvre son menu Démarrer quand la touche Windows est relâchée sans qu'aucune autre touche n'ait été vue entre-temps, et son
    // sélecteur quand il voit Alt+Tab. Pour les remplacer, on retire nos touches du flux, puis on glisse une touche neutre (VK 0xE8, non
    // attribuée) avant le relâchement de Windows ou d'Alt : sans elle, Windows croirait à un simple appui et ouvrirait son menu.
    // Fusible : un fichier %LOCALAPPDATA%\CdxShell\sans-clavier.txt empêche l'installation du crochet.
    bool winDown, winOther, winHandled, altDown, altSwallowed, switcherOpen;
    List<IntPtr> swItems = new(); int swSel;

    void InstallKeyboardHook()
    {
        if (File.Exists(Path.Combine(Host.DataDir, "sans-clavier.txt"))) { Host.Log("crochet clavier désactivé par sans-clavier.txt"); return; }
        kbdHook = new LowLevelHook(13 /* WH_KEYBOARD_LL */, KeyHook, "crochet clavier");   // KeyHook tourne sur ce fil : il ne touche aux fenêtres que par Bar.BeginInvoke
    }

    static void MaskKey()
    {
        var down = new INPUT { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0xE8 } } };
        var up = new INPUT { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0xE8, dwFlags = 2 /* KEYEVENTF_KEYUP */ } } };
        N.SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
    }

    IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if ((k.flags & 0x10 /* LLKHF_INJECTED */) == 0)   // nos propres touches (la touche neutre) ne repassent pas ici
                {
                    int msg = (int)wParam; uint vk = k.vkCode;
                    bool down = msg == 0x0100 || msg == 0x0104, up = msg == 0x0101 || msg == 0x0105;
                    if (vk == 0x5B || vk == 0x5C)   // touche Windows
                    {
                        if (down) { if (!winDown) { winDown = true; winOther = false; winHandled = false; } }
                        else if (up)
                        {
                            bool tap = winDown && !winOther, handled = winHandled; winDown = false;
                            if (tap || handled) MaskKey();
                            if (tap) Bar.BeginInvoke(new Action(() => { Host.Log("touche Windows : menu Démarrer CDX"); if (startOpen) HideStart(); else ShowStart(); }));
                        }
                    }
                    else if (vk == 0xA4 || vk == 0xA5 || vk == 0x12)   // Alt
                    {
                        if (down) altDown = true;
                        else if (up)
                        {
                            altDown = false;
                            if (altSwallowed)
                            {
                                altSwallowed = false; MaskKey();   // sans cela, Alt relâché seul activerait la barre de menus de la fenêtre qui reçoit le focus
                                if (switcherOpen) { switcherOpen = false; Bar.BeginInvoke(new Action(() => CommitSwitcher(IntPtr.Zero))); }
                            }
                        }
                    }
                    else if (down)
                    {
                        if (winDown)
                        {
                            winOther = true;
                            if (vk == 0x45 /* E */) { winHandled = true; Bar.BeginInvoke(new Action(() => { Host.Log("Win+E : Explorateur CDX"); Launch("cdx:explorer"); })); return (IntPtr)1; }
                            if (vk == 0x54 /* T */) { winHandled = true; Bar.BeginInvoke(new Action(() => { Host.Log("Win+T : Terminal CDX"); Launch("cdx:terminal"); })); return (IntPtr)1; }   // la touche T de Windows fait défiler les boutons de sa barre : sans objet ici
                        }
                        else if (vk == 0x09 /* Tab */ && (altDown || (k.flags & 0x20 /* LLKHF_ALTDOWN */) != 0) && (N.GetAsyncKeyState(0x11) & 0x8000) == 0)
                        {
                            altSwallowed = true;
                            bool shift = (N.GetAsyncKeyState(0x10) & 0x8000) != 0;
                            if (!switcherOpen) { switcherOpen = true; Bar.BeginInvoke(new Action(() => OpenSwitcher(shift))); }
                            else Bar.BeginInvoke(new Action(() => MoveSwitcher(shift ? -1 : 1)));
                            return (IntPtr)1;
                        }
                        else if (switcherOpen && altDown)
                        {
                            if (vk == 0x1B /* Échap */) { switcherOpen = false; Bar.BeginInvoke(new Action(HideSwitcher)); return (IntPtr)1; }
                            if (vk == 0x27 /* → */ || vk == 0x25 /* ← */) { int d = vk == 0x27 ? 1 : -1; Bar.BeginInvoke(new Action(() => MoveSwitcher(d))); return (IntPtr)1; }
                            if (vk == 0x0D /* Entrée */) { switcherOpen = false; Bar.BeginInvoke(new Action(() => CommitSwitcher(IntPtr.Zero))); return (IntPtr)1; }
                        }
                    }
                }
            }
        }
        catch (Exception ex) { Host.Log("crochet clavier : " + ex.Message); }
        return N.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    // Les fenêtres dans l'ordre où Alt+Tab les présente : l'ordre d'empilement de Windows (la plus récemment utilisée d'abord).
    List<IntPtr> ZOrderTasks() { var l = new List<IntPtr>(); N.EnumWindows((h, _) => { if (IsTaskWindow(h)) l.Add(h); return true; }, IntPtr.Zero); return l; }

    void OpenSwitcher(bool backwards)
    {
        swItems = ZOrderTasks();
        if (swItems.Count == 0) { switcherOpen = false; return; }
        swSel = swItems.Count > 1 ? (backwards ? swItems.Count - 1 : 1) : 0;
        var scr = Screen.PrimaryScreen.WorkingArea;
        int cardW = (int)(266 * scale), cardH = (int)(206 * scale);
        int cols = Math.Max(1, Math.Min(Math.Min(swItems.Count, 6), (scr.Width - (int)(120 * scale)) / cardW));
        int rows = (swItems.Count + cols - 1) / cols;
        int w = cols * cardW + (int)(46 * scale), h = rows * cardH + (int)(92 * scale);
        Switch.Bounds = new Rectangle(scr.Left + (scr.Width - w) / 2, scr.Top + (scr.Height - h) / 2, w, h);
        Switch.Send(new
        {
            type = "items", cols, sel = swSel,
            items = swItems.Select(x => new { h = x.ToInt64().ToString(), title = N.TitleOf(x), exe = N.ExePathOf(x) ?? "", min = N.IsIconic(x) })
        });
        Switch.Show();
        Host.Log($"Alt+Tab : {swItems.Count} fenêtres, sélection {swSel}");
    }

    void MoveSwitcher(int d)
    {
        if (swItems.Count == 0) return;
        swSel = (swSel + d + swItems.Count) % swItems.Count;
        Switch.Send(new { type = "sel", index = swSel });
    }

    void HideSwitcher() { switcherOpen = false; Switch.Hide(); }

    // h = IntPtr.Zero : la fenêtre choisie au clavier ; sinon celle sur laquelle on a cliqué.
    void CommitSwitcher(IntPtr h)
    {
        IntPtr target = h != IntPtr.Zero ? h : (swItems.Count > 0 ? swItems[Math.Min(swSel, swItems.Count - 1)] : IntPtr.Zero);
        switcherOpen = false; Switch.Hide();
        if (target != IntPtr.Zero) { Host.Log("Alt+Tab : activation de « " + N.TitleOf(target) + " »"); ForceForeground(target); }
    }
}
