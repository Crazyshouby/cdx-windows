using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cdx;

// Les boutons latéraux de la souris (précédent / suivant). Les faire passer par le contrôle WebView2 est fragile (Chromium les traite lui-même, et selon
// le pilote de la souris Windows les convertit ou non en WM_APPCOMMAND) : un crochet souris global les voit en premier. Au-dessus d'une de nos fenêtres
// (`windows` en rend la liste), le bouton 1 (précédent) et le bouton 2 (suivant) sont envoyés à la page et retirés du flux ; partout ailleurs, rien ne change.
sealed class SideButtons : IDisposable
{
    readonly Func<IEnumerable<HostForm>> windows;
    readonly SynchronizationContext ui;   // le fil de l'interface : seul à toucher aux fenêtres
    readonly uint pid = (uint)Environment.ProcessId;
    readonly LowLevelHook hook;

    // À construire sur le fil de l'interface (son contexte de synchronisation reçoit les boutons). Le crochet, lui, vit sur un fil à part : quand l'appli est
    // occupée (une grosse suppression, une liste énorme), la souris de tout Windows ne doit pas attendre qu'elle se libère.
    public SideButtons(Func<IEnumerable<HostForm>> windows)
    {
        this.windows = windows;
        ui = SynchronizationContext.Current;
        hook = new LowLevelHook(14 /* WH_MOUSE_LL */, Hook, "crochet souris");
    }

    // Appelé sur le fil du crochet pour CHAQUE évènement souris du système : ne rien faire de long ici, ne toucher à aucune fenêtre (elles appartiennent au fil de l'interface).
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
                N.GetWindowThreadProcessId(root, out uint owner);
                bool ours = owner == pid && ui != null;   // une de nos fenêtres : laquelle, c'est le fil de l'interface qui le dira
                if (msg == 0x020C) Host.Log($"bouton latéral {which} : {(ours ? "sur une fenêtre " + Host.AppName : "ailleurs (" + N.TitleOf(root) + ")")}");
                if (ours)
                {
                    if (msg == 0x020C) ui.Post(_ =>
                    {
                        try { windows().FirstOrDefault(x => !x.IsDisposed && x.IsHandleCreated && x.Handle == root)?.Send(new { type = "nav", dir = which == 1 ? "back" : "fwd" }); }
                        catch (Exception ex) { Host.Log("bouton latéral : " + ex.Message); }
                    }, null);
                    return (IntPtr)1;   // retiré du flux : ni Chromium ni Windows ne le traitent en plus
                }
            }
        }
        catch (Exception ex) { Host.Log("crochet souris : " + ex.Message); }
        return hook?.Next(code, wParam, lParam) ?? N.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose() => hook.Dispose();
}
