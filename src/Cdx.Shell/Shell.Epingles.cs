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

// Les applications épinglées (barre et menu Démarrer) et le menu d'un bouton de la barre. Sorti de Shell.cs (audit du 3/11/2026, 4.6).
partial class Shell
{
    // ---------- applications épinglées ----------
    // Le shell détient la liste (epingles.json) ; la barre affiche celles qui ont Bar, le menu Démarrer celles qui ont Tile.
    object PinsMessage() => new
    {
        type = "pins",
        items = pinStore.Items.Select(p => new { name = p.Name, target = p.Target, ic = p.Ic, bar = p.Bar, tile = p.Tile, barIndex = pinStore.BarIndex(p.Target), path = File.Exists(p.Target) ? p.Target : null }).ToList()
    };

    void PushPins() { var m = PinsMessage(); Bar.Send(m); Start.Send(m); }

    // Le nom d'une épingle : la description du programme (Chrome, Bloc-notes…), sinon le nom du fichier ou du dossier sans extension.
    static string PinNameOf(string path)
    {
        string name = Directory.Exists(path) ? new DirectoryInfo(path).Name : Path.GetFileNameWithoutExtension(path);
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { try { var d = FileVersionInfo.GetVersionInfo(path).FileDescription; if (!string.IsNullOrWhiteSpace(d)) name = d.Trim(); } catch { } }
        return string.IsNullOrWhiteSpace(name) ? path : (name.Length > 80 ? name[..80] : name);
    }

    // Des fichiers déposés sur la barre (depuis l'Explorateur CDX ou celui de Windows) : seuls les programmes, raccourcis, scripts et dossiers sont épinglés.
    void PinDropped(string[] paths, string before)
    {
        if (paths == null || paths.Length == 0) return;
        int n = 0, skipped = 0;
        foreach (var path in paths.Take(20))
        {
            if (!PinStore.Pinnable(path)) { skipped++; continue; }
            if (pinStore.AddToBar(PinNameOf(path), path, null, before)) n++; else skipped++;
        }
        Host.Log($"dépôt sur la barre : {n} épinglée(s), {skipped} ignoré(s)");
        if (n > 0) PushPins();
    }

    // Une fenêtre de la barre glissée parmi les épingles : on épingle son programme.
    void PinTask(string h, string before)
    {
        if (!long.TryParse(h, out long hv)) return;
        var hw = new IntPtr(hv); if (!N.IsWindow(hw)) return;
        string exe = N.ExePathOf(hw);
        if (string.IsNullOrEmpty(exe) || exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) return;
        if (pinStore.AddToBar(PinNameOf(exe), exe, null, before)) { Host.Log("fenêtre épinglée par glisser : " + exe); PushPins(); }
    }

    // Le menu d'un bouton de la barre : « pin » (clé = cible de l'épingle) ou « task » (clé = poignée de la fenêtre).
    void OpenBarMenu(string kind, string key, int x)
    {
        var items = new List<(string id, string label)>(); Action<string> act; string title;
        var marks = new Dictionary<string, (string mark, bool faint)>();   // repère « ▶ » de la fenêtre active, texte pâle des fenêtres réduites
        if (kind == "pin")
        {
            var p = pinStore.Find(key); if (p == null || !p.Bar) return;
            var bar = pinStore.OnBar; int k = bar.FindIndex(q => q.Target == p.Target);
            title = p.Name;
            items.Add(("open", "Ouvrir")); items.Add(("remove", "Retirer de la barre"));
            if (k > 0) items.Add(("left", "Déplacer à gauche"));
            if (k < bar.Count - 1) items.Add(("right", "Déplacer à droite"));
            act = id =>
            {
                switch (id)
                {
                    case "open": Launch(p.Target); break;
                    case "remove": if (pinStore.RemoveFromBar(p.Target)) PushPins(); break;
                    case "left": if (pinStore.Move(p.Target, -1)) PushPins(); break;
                    case "right": if (pinStore.Move(p.Target, 1)) PushPins(); break;
                }
            };
        }
        else if (kind == "task" && long.TryParse(key, out long hv))
        {
            var h = new IntPtr(hv); if (!N.IsWindow(h)) return;
            string exe = N.ExePathOf(h);
            bool pinnable = !string.IsNullOrEmpty(exe) && !exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe);
            var existing = pinnable ? pinStore.Find(exe) : null;
            title = N.TitleOf(h); if (title.Length > 40) title = title[..40] + "…";
            if (pinnable) items.Add(existing is { Bar: true } ? ("unpin", "Détacher de la barre") : ("pin", "Épingler à la barre"));
            items.Add(("activate", "Afficher")); items.Add(("close", "Fermer la fenêtre"));
            act = id =>
            {
                switch (id)
                {
                    case "pin":
                        string name = PinNameOf(exe);
                        if (pinStore.AddToBar(name, exe)) { Host.Log("épinglée à la barre : " + name); PushPins(); }
                        break;
                    case "unpin": if (pinStore.RemoveFromBar(exe)) PushPins(); break;
                    case "activate": Activate(h); break;
                    case "close": N.PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); break;
                }
            };
        }
        else if (kind is "windows" or "group")
        {
            // Un bouton qui réunit plusieurs fenêtres d'une application : « windows » (clic) en liste les fenêtres, « group » (clic droit) offre épingler / tout fermer.
            var wins = TaskWins().Where(t => TaskGrouping.KeyOf(t) == key).ToList(); if (wins.Count == 0) return;
            string exe = wins[0].Exe; title = AppNameOf(exe);
            if (kind == "windows")
            {
                foreach (var t in wins.Take(60))
                {
                    string id = "w:" + t.H.ToInt64(), label = string.IsNullOrWhiteSpace(t.Title) ? title : (t.Title.Length > 70 ? t.Title[..70] + "…" : t.Title);
                    items.Add((id, label)); marks[id] = (t.Active ? "▶" : "", t.Min);
                }
                act = id => { if (id.StartsWith("w:") && long.TryParse(id[2..], out long wv) && N.IsWindow(new IntPtr(wv))) ForceForeground(new IntPtr(wv)); };
            }
            else
            {
                bool pinnable = File.Exists(exe); var existing = pinnable ? pinStore.Find(exe) : null;
                if (pinnable) items.Add(existing is { Bar: true } ? ("unpin", "Détacher de la barre") : ("pin", "Épingler à la barre"));
                items.Add(("closeall", $"Fermer toutes les fenêtres ({wins.Count})"));
                var handles = wins.Select(t => t.H).ToList();
                act = id =>
                {
                    switch (id)
                    {
                        case "pin": if (pinStore.AddToBar(AppNameOf(exe), exe)) { Host.Log("épinglée à la barre : " + exe); PushPins(); } break;
                        case "unpin": if (pinStore.RemoveFromBar(exe)) PushPins(); break;
                        case "closeall": foreach (var wh in handles) N.PostMessage(wh, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); break;
                    }
                };
            }
        }
        else return;
        if (startOpen) HideStart(); if (centerOpen) HideCenter(); HideAudioMenu();
        barMenuAct = act;
        int w = (int)(280 * scale), hgt = (int)((21 + 12 + 4 + Math.Min(items.Count, 14) * 27) * scale), m = (int)(6 * scale);   // au-delà de 14 lignes, la liste défile
        var scr = Screen.FromControl(Bar).Bounds;
        BarMenu.Bounds = new Rectangle(Math.Clamp(Bar.Left + x, scr.Left, scr.Right - w), Bar.Top - m - hgt, w, hgt);
        BarMenu.Send(new { type = "items", title, items = items.Select(i => new { id = i.id, label = i.label, mark = marks.TryGetValue(i.id, out var mk) ? mk.mark : "", faint = marks.TryGetValue(i.id, out var mf) && mf.faint }).ToList() });
        BarMenu.Show(); BarMenu.Activate(); ForceForeground(BarMenu.Handle);
        BarMenu.Send(new { type = "show" });
        barMenuOpen = true;
        Host.Log("menu de la barre ouvert : " + kind);
    }

    void HideBarMenu()
    {
        if (!barMenuOpen) return;
        barMenuOpen = false; barMenuAct = null;
        BarMenu.Hide();
    }

    public void OnBarMenuDeactivated() { if (barMenuOpen) HideBarMenu(); }
}
