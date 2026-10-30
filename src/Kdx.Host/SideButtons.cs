using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Kdx;

// Les boutons latéraux de la souris (précédent / suivant). Les faire passer par le contrôle WebView2 est fragile (Chromium les traite lui-même, et selon
// le pilote de la souris Windows les convertit ou non en WM_APPCOMMAND) : un crochet souris global les voit en premier. Au-dessus d'une de nos fenêtres
// (`windows` en rend la liste), le bouton 1 (précédent) et le bouton 2 (suivant) sont envoyés à la page et retirés du flux ; partout ailleurs, rien ne change.
sealed class SideButtons : IDisposable
{
    readonly Func<IEnumerable<HostForm>> windows;
    readonly N.HookProc proc;   // le délégué est gardé en champ : le ramasse-miettes ne doit pas le libérer
    IntPtr hook;

    public SideButtons(Func<IEnumerable<HostForm>> windows)
    {
        this.windows = windows;
        proc = Hook;
        hook = N.SetWindowsHookEx(14 /* WH_MOUSE_LL */, proc, N.GetModuleHandle(null), 0);
        Host.Log(hook == IntPtr.Zero ? $"crochet souris refusé (erreur {Marshal.GetLastWin32Error()})" : "crochet souris installé");
    }

    IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            int msg = (int)wParam;
            if (code >= 0 && (msg == 0x020B /* WM_XBUTTONDOWN */ || msg == 0x020C /* WM_XBUTTONUP */))
            {
                var h = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int which = (int)((h.mouseData >> 16) & 0xFFFF);   // 1 = précédent, 2 = suivant
                var root = N.GetAncestor(N.WindowFromPoint(h.pt), 2 /* GA_ROOT */);
                var f = windows().FirstOrDefault(x => x.IsHandleCreated && x.Handle == root);
                if (msg == 0x020C) Host.Log($"bouton latéral {which} : {(f != null ? "sur une fenêtre " + Host.AppName : "ailleurs (" + N.TitleOf(root) + ")")}");
                if (f != null)
                {
                    if (msg == 0x020C) f.Send(new { type = "nav", dir = which == 1 ? "back" : "fwd" });
                    return (IntPtr)1;   // retiré du flux : ni Chromium ni Windows ne le traitent en plus
                }
            }
        }
        catch (Exception ex) { Host.Log("crochet souris : " + ex.Message); }
        return N.CallNextHookEx(hook, code, wParam, lParam);
    }

    public void Dispose() { if (hook != IntPtr.Zero) { N.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } }
}
