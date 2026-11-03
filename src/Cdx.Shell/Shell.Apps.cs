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

// Les applications du menu Démarrer : les raccourcis des dossiers « Programmes », surveillés. Sorti de Shell.cs (audit du 3/11/2026, 4.6).
partial class Shell
{
    // ---------- applications et icônes ----------
    // Le menu Démarrer suit les installations : un raccourci ajouté, retiré ou renommé dans les dossiers « Programmes » (utilisateur, commun) renvoie la liste à la page,
    // 1,5 s après le dernier changement (un installeur en écrit plusieurs d'affilée).
    readonly List<FileSystemWatcher> appWatchers = new();
    System.Windows.Forms.Timer appsTimer;
    void WatchStartMenu()
    {
        if (appWatchers.Count > 0) return;
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var w = new FileSystemWatcher(dir, "*.lnk") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
                FileSystemEventHandler h = (s, e) => { try { Bar.BeginInvoke(new Action(AppsChanged)); } catch (InvalidOperationException) { } };
                w.Created += h; w.Deleted += h; w.Changed += h; w.Renamed += (s, e) => h(s, e);
                w.EnableRaisingEvents = true; appWatchers.Add(w);
            }
            catch (Exception ex) { Host.Log("menu Démarrer non surveillé (" + dir + ") : " + ex.Message); }
        }
    }
    void AppsChanged()
    {
        if (appsTimer == null) { appsTimer = new System.Windows.Forms.Timer { Interval = 1500 }; appsTimer.Tick += (s, e) => { appsTimer.Stop(); var items = ScanApps(); Start.Send(new { type = "apps", items }); Host.Log("menu Démarrer : applications relues (" + items.Count + ")"); }; }
        appsTimer.Stop(); appsTimer.Start();
    }

    static List<object> ScanApps()
    {
        var map = new SortedDictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
            {
                string n = Path.GetFileNameWithoutExtension(f);
                if (Regex.IsMatch(n, "uninstall|d[ée]sinstall", RegexOptions.IgnoreCase)) continue;
                map.TryAdd(n, f);
            }
        }
        return map.Select(kv => (object)new { name = kv.Key, path = kv.Value }).ToList();
    }
}
