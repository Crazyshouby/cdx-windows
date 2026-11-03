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

partial class Shell : IHostApp
{
    public readonly BarForm Bar;
    public readonly PopupForm Start;
    public readonly SwitcherForm Switch;
    public readonly PopupForm Center;
    public readonly PopupForm AudioMenu;
    public readonly PopupForm BarMenu;   // le menu contextuel d'un bouton de la barre (épingles, fenêtres), ouvert au-dessus de la barre
    public uint CallbackMsg;
    readonly PinStore pinStore;
    bool barMenuOpen; Action<string>? barMenuAct;   // null : aucun menu de la barre ouvert

    string theme; readonly string root;
    readonly bool pinnedTheme;   // un thème donné en argument : la suite ne le change pas
    IDisposable? suiteWatch;   // null : thème imposé en argument, ou pas encore initialisé
    CoreWebView2Environment? env;
    double scale = 1; int barH = 36;
    IntPtr lastFg;
    readonly Dictionary<IntPtr, int> order = new(); int seq;
    LowLevelHook? kbdHook;   // sur un fil à lui : la barre occupée ne doit jamais faire ramer le clavier de tout Windows
    bool startOpen, centerOpen, cleaned; DateTime startHiddenAt = DateTime.MinValue, centerHiddenAt = DateTime.MinValue;
    long pIdle, pKernel, pUser; int tick;
    bool audioMenuOpen; DateTime audioMenuHiddenAt = DateTime.MinValue; volatile object? lastAudio;
    AudioLink? audio;   // la liaison avec Audio Linker (tube « cdx.audio_linker »), si ce programme tourne
    TrayRelay? tray;    // la zone de notification : les icônes des applis, reçues à la place de la barre de Windows

    public Shell(string? theme)
    {
        pinnedTheme = theme != null; this.theme = theme ?? Suite.ReadAccent(); root = Host.FindWebRoot();
        Bar = new BarForm(this); Start = new PopupForm(this, "start", OnStartDeactivated); Switch = new SwitcherForm(this); Center = new PopupForm(this, "center", OnCenterDeactivated); AudioMenu = new PopupForm(this, "audiomenu", OnAudioMenuDeactivated); BarMenu = new PopupForm(this, "barmenu", OnBarMenuDeactivated);
        pinStore = new PinStore(Path.Combine(Host.DataDir, "epingles.json"));
        Bar.Load += async (s, e) => { try { await InitAsync(); } catch (Exception ex) { Host.Log("initialisation échouée : " + ex); } };
    }

    async Task InitAsync()
    {
        scale = Bar.DeviceDpi / 96.0; barH = (int)Math.Round(36 * scale);
        CallbackMsg = N.RegisterWindowMessage("CdxShellAppBar");
        var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = Bar.Handle, uCallbackMessage = CallbackMsg };
        N.SHAppBarMessage(N.ABM_NEW, ref abd);
        PlaceBar(ref abd);
        TaskbarState.AutoHide();
        InstallKeyboardHook();
        Host.StartControlPipe("shell", Bar, HandleControl, () => cleaned);

        env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Host.DataDir, "webview"), new CoreWebView2EnvironmentOptions("--disable-lcd-text"));
        await Bar.InitWebAsync(env, root, "shell/bar.html?theme=" + theme, scale);
        audio = new AudioLink(o => { lastAudio = o; try { Bar.BeginInvoke(new Action(() => { Bar.Send(o); AudioMenu.Send(o); })); } catch { } });
        audio.Start();
        _ = Start.Handle;   // la poignée doit exister avant l'initialisation de WebView2 (la fenêtre reste cachée)
        await Start.InitWebAsync(env, root, "shell/start.html?theme=" + theme, scale);
        _ = Center.Handle;
        await Center.InitWebAsync(env, root, "shell/center.html?theme=" + theme, scale);
        _ = Switch.Handle;
        await Switch.InitWebAsync(env, root, "shell/alttab.html?theme=" + theme, scale);
        _ = AudioMenu.Handle;
        await AudioMenu.InitWebAsync(env, root, "shell/audiomenu.html?theme=" + theme, scale);
        _ = BarMenu.Handle;
        await BarMenu.InitWebAsync(env, root, "shell/barmenu.html?theme=" + theme, scale);

        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (s, e) => Tick();
        timer.Start();
        Bus.Listen("shell", OnBus);
        if (Environment.ProcessPath is { } self) Suite.RegisterApp("shell", self);   // ProcessPath peut manquer (rare) : rien n'est alors inscrit, plutôt qu'un chemin null écrit par-dessus le bon dans apps.json
        // Les deux autres programmes sont posés à côté du shell : leur chemin est enregistré même s'ils n'ont pas encore tourné (ou si apps.json garde un ancien dossier).
        foreach (var other in SuiteApps.All.Where(a => a.Name != "shell")) { var exe = Path.Combine(AppContext.BaseDirectory, other.Exe + ".exe"); if (File.Exists(exe)) Suite.RegisterApp(other.Name, exe); }   // les voisins posés à côté du shell (SuiteApps.cs)
        if (!pinnedTheme) suiteWatch = Suite.Watch(theme, a => { theme = a; foreach (var f in new HostForm[] { Bar, Start, Center, Switch, AudioMenu, BarMenu }) f.Send(new { type = "theme", value = a }); Host.Log("accent de la suite : " + a); });
        StartTray();
        Host.Log($"shell prêt : thème {theme}, échelle {scale}, racine {root}");
    }

    // Fusible : un fichier %LOCALAPPDATA%\CdxShell\sans-zone.txt empêche le shell de prendre en charge la zone de notification (Windows garde la sienne).
    void StartTray()
    {
        if (File.Exists(Path.Combine(Host.DataDir, "sans-zone.txt"))) { Host.Log("zone de notification désactivée par sans-zone.txt"); return; }
        var t = new TrayRelay { OnBalloon = (title, text, exe) => AddBalloon(title, text, exe) };
        if (!t.Create(Bar.Bounds)) return;
        tray = t; t.Announce();
        Host.Log("zone de notification : relais en place, applis invitées à se réinscrire");
    }

    void SendTray() => Bar.Send(new { type = "tray", items = tray?.Items() ?? new List<object>() });

    // Une bulle d'une appli (NIF_INFO) devient une notification du centre rapide.
    void AddBalloon(string title, string text, string exe)
    {
        if (string.IsNullOrWhiteSpace(title)) title = exe.Length > 0 ? exe : "Notification";
        AddNotification(title.Length > 80 ? title[..80] : title, text.Length > 400 ? text[..400] : text, "info", exe);
    }

    void PlaceBar(ref APPBARDATA abd)
    {
        var scr = Screen.PrimaryScreen!.Bounds;   // « ! » : le shell tourne dans une session ouverte, Windows y a toujours un écran principal
        abd.uEdge = N.ABE_BOTTOM;
        abd.rc = new RECT { Left = scr.Left, Right = scr.Right, Top = scr.Bottom - barH, Bottom = scr.Bottom };
        tray?.Suspend();   // les messages de barre d'application doivent atteindre la barre de Windows, pas le relais de la zone de notification
        try
        {
            N.SHAppBarMessage(N.ABM_QUERYPOS, ref abd);
            abd.rc.Top = abd.rc.Bottom - barH;
            N.SHAppBarMessage(N.ABM_SETPOS, ref abd);
        }
        finally { tray?.Resume(); }
        Bar.Bounds = Rectangle.FromLTRB(abd.rc.Left, abd.rc.Top, abd.rc.Right, abd.rc.Bottom);
        Host.Log($"barre placée : {Bar.Bounds}");
        tray?.Place(Bar.Bounds);
    }


    public void OnAppBarMessage(int code)
    {
        if (code != 1 /* ABN_POSCHANGED */) return;
        var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = Bar.Handle, uCallbackMessage = CallbackMsg };
        PlaceBar(ref abd);
    }

    public void Cleanup()
    {
        if (cleaned) return; cleaned = true;
        try { audio?.Stop(); suiteWatch?.Dispose(); } catch { }
        try { tray?.Shutdown(); } catch { }   // avant les appels de barre d'application, qui doivent atteindre la barre de Windows
        try { var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = Bar.Handle }; N.SHAppBarMessage(N.ABM_REMOVE, ref abd); } catch { }
        kbdHook?.Dispose(); kbdHook = null;
        TaskbarState.Restore();
    }

    // ---------- messages venus des pages ----------
    public void OnMessage(HostForm from, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string cmd = r.GetProperty("cmd").GetString() ?? throw new FormatException("cmd null");   // rattrapé plus bas, comme l'était le déréférencement
            string? Str(string k) => r.TryGetProperty(k, out var v) ? v.GetString() : null;
            string Req(string k) => Str(k) ?? throw new FormatException(k + " manquant");   // rattrapé plus bas, comme l'était l'ArgumentNullException de long.Parse ou du lancement
            if (Host.CommonMessage(from, cmd, r)) return;
            switch (cmd)
            {
                case "ready":
                    Host.Log($"page {Str("page")} prête");
                    if (Str("page") == "bar") { Tick(); SendStats(); audio?.Refresh(); PushNotifs(); SendTray(); from.Send(PinsMessage()); }
                    else if (Str("page") == "audiomenu") { if (lastAudio != null) from.Send(lastAudio); }
                    else if (Str("page") == "barmenu") { }
                    else { from.Send(new { type = "user", name = Environment.UserName }); from.Send(new { type = "apps", items = ScanApps() }); from.Send(PinsMessage()); WatchStartMenu(); }
                    break;
                case "pin.add": if (pinStore.AddToBar(Str("name"), Str("target"), null, Str("before"))) { Host.Log("épinglée à la barre : " + Str("name")); PushPins(); } break;
                case "pin.reorder": if (pinStore.MoveTo(Str("target") ?? "", Str("before"))) PushPins(); break;
                case "pin.dropfiles": PinDropped(from.DroppedPaths, Str("before")); break;
                case "pin.addtask": PinTask(Str("h"), Str("before")); break;
                case "pin.remove": if (pinStore.RemoveFromBar(Str("target") ?? "")) { Host.Log("retirée de la barre : " + Str("target")); PushPins(); } break;
                case "bar.menu": OpenBarMenu(Str("kind"), Str("key") ?? "", r.TryGetProperty("x", out var bx) && bx.TryGetInt32(out var bxi) ? bxi : 0); break;
                case "barmenu.pick": { var act = barMenuAct; HideBarMenu(); act?.Invoke(Str("id") ?? ""); break; }
                case "hideBarMenu": HideBarMenu(); break;
                case "alttab.pick": CommitSwitcher(new IntPtr(long.Parse(Req("h")))); break;
                case "launch": Launch(Req("target")); break;
                case "activate": Activate(new IntPtr(long.Parse(Req("h")))); break;
                case "close": N.PostMessage(new IntPtr(long.Parse(Req("h"))), 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); break;
                case "toggleStart": if (startOpen) HideStart(); else if ((DateTime.UtcNow - startHiddenAt).TotalMilliseconds > 250) ShowStart(); break;
                case "hideStart": HideStart(); break;
                case "toggleCenter": if (centerOpen) HideCenter(); else if ((DateTime.UtcNow - centerHiddenAt).TotalMilliseconds > 250) ShowCenter(); break;
                case "hideCenter": HideCenter(); break;
                case "notify.clear": notifications.Clear(); unreadNotifs = 0; PushNotifs(); break;
                case "center.get": from.Send(SysInfo.Center()); break;
                case "audio.menu": if (audioMenuOpen) HideAudioMenu(); else if ((DateTime.UtcNow - audioMenuHiddenAt).TotalMilliseconds > 250) ShowAudioMenu(r.TryGetProperty("x", out var ax) && ax.TryGetInt32(out var axi) ? axi : 0); break;
                case "hideAudioMenu": HideAudioMenu(); break;
                case "audio.toggle": audio?.Toggle(); break;
                case "audio.mute": audio?.Mute(r.GetProperty("on").GetBoolean()); break;
                case "audio.volume": audio?.Volume(r.GetProperty("v").GetInt32(), r.TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.True); break;
                case "audio.setup": audio?.Setup(Str("name")); break;
                case "vol.set": try { SysInfo.SetVolume(r.GetProperty("v").GetInt32()); } catch (Exception ex) { Host.Log("volume : " + ex.Message); } break;
                case "vol.mute": try { SysInfo.SetMute(r.GetProperty("on").GetBoolean()); } catch (Exception ex) { Host.Log("coupure du son : " + ex.Message); } from.Send(SysInfo.Center()); break;
                case "power": HideStart(); SysInfo.Power(Str("action")); break;
                case "lock": N.LockWorkStation(); break;
                case "tray.click": tray?.Click(Str("k") ?? "", Str("b") ?? ""); break;
                case "quit": Host.Log("arrêt demandé depuis le menu"); Bar.Close(); break;
            }
        }
        catch (Exception ex) { Host.Log("message mal formé ou échec : " + ex.Message + " :: " + json); }
    }

    // Les messages du bus (SUITE.md §3.1) : notify { title, text, level? }. Les notifications sont gardées (50 au plus) et poussées au centre rapide.
    readonly List<object> notifications = new();
    int unreadNotifs;   // non vues : on les compte jusqu'à l'ouverture du centre rapide

    void PushNotifs() { Bar.Send(new { type = "notifs", unread = unreadNotifs }); Center.Send(new { type = "notify", items = notifications }); }

    bool OnBus(string type, string from, JsonElement data)
    {
        if (type != "notify") return false;
        // « ! » : une valeur JSON de type texte ne se lit jamais null
        string? title = data.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        string text = data.TryGetProperty("text", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString()! : "";
        string level = data.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : "info";
        if (string.IsNullOrEmpty(title) || title.Length > 80 || text.Length > 400 || level is not ("info" or "warn" or "error")) return false;
        AddNotification(title, text, level, from);
        return true;
    }

    void AddNotification(string title, string text, string level, string from)
    {
        var n = new { title, text, level, from, at = DateTimeOffset.Now.ToUnixTimeSeconds() };
        notifications.Add(n); if (notifications.Count > 50) notifications.RemoveAt(0);
        if (!centerOpen) unreadNotifs++;
        Host.Log($"notification de {from} : {title}");
        PushNotifs();
    }

    async Task<string> HandleControl(string line)
    {
        int sp = line.IndexOf(' '); string c = sp < 0 ? line : line[..sp], a = sp < 0 ? "" : line[(sp + 1)..];
        switch (c)
        {
            case "state": return JsonSerializer.Serialize(new { pins = pinStore.Items.Count, onBar = pinStore.OnBar.Count, notifications = notifications.Count, unread = unreadNotifs, audio = audio == null ? "aucune liaison" : audio.Connected ? "connecté" : "déconnecté", tray = tray?.Count ?? 0 });
            case "launch": Launch(a); return "ok";
            case "evalbar": return await Bar.Web.CoreWebView2.ExecuteScriptAsync(a);   // exécute du JavaScript dans la page de la barre (tests)
            case "notifs": return notifications.Count + "/" + unreadNotifs;
            case "pins": return string.Join(" ; ", pinStore.Items.Select(p => (p.Bar ? "B" : "-") + (p.Tile ? "T" : "-") + " " + p.Target));
            case "barmenu": { var parts = a.Split(' ', 3); OpenBarMenu(parts[0], parts.Length > 1 ? parts[1] : "", 0); return barMenuOpen ? "ouvert" : "refusé"; }
            case "barmenupick": { var act = barMenuAct; HideBarMenu(); act?.Invoke(a); return act == null ? "pas de menu" : "ok"; }
            case "tray": return tray == null ? "pas de relais" : tray.Count + " reçues : " + JsonSerializer.Serialize(tray.Describe());
            case "trayclick": { var parts = a.Split(' '); return tray != null && parts.Length == 2 && tray.Click(parts[0], parts[1]) ? "ok" : "refusé"; }
            case "audio": return audio == null ? "pas de liaison" : audio.Connected ? "connecté" : "déconnecté";
            default: return "commande inconnue";
        }
    }

    void ShowCenter()
    {
        if (startOpen) HideStart();
        int w = (int)(380 * scale), h = (int)(660 * scale), m = (int)(6 * scale);
        Center.Bounds = new Rectangle(Bar.Right - w - m, Bar.Top - m - h, w, h);
        Center.Show(); Center.Activate(); ForceForeground(Center.Handle);
        Center.Send(new { type = "show" });
        centerOpen = true; unreadNotifs = 0; PushNotifs(); Bar.Send(new { type = "centerOpen", open = true });
        Host.Log("centre rapide ouvert");
    }

    void HideCenter()
    {
        if (!centerOpen) return;
        centerOpen = false; centerHiddenAt = DateTime.UtcNow;
        Center.Send(new { type = "hide" }); Center.Hide(); Bar.Send(new { type = "centerOpen", open = false });
        Host.Log("centre rapide fermé");
    }

    public void OnCenterDeactivated() { if (centerOpen) HideCenter(); }

    // Les setups d'Audio Linker, dans une petite fenêtre posée au-dessus de la barre, alignée sur le bord gauche du bouton (x : pixels de la page de la barre).
    void ShowAudioMenu(int x)
    {
        if (startOpen) HideStart(); if (centerOpen) HideCenter();
        int rows = 1; try { using var d = JsonDocument.Parse(JsonSerializer.Serialize(lastAudio)); if (d.RootElement.TryGetProperty("setups", out var s)) rows = Math.Clamp(s.GetArrayLength(), 1, 14); } catch { }
        int w = (int)(280 * scale), h = (int)((21 + 8 + 24 + 8 + 24 + 8 + 2 + 8 + 16 + 8 + rows * 27 + 4 + 18) * scale), m = (int)(6 * scale);   // titre, état, volume, séparateur, étiquette, liste (audiomenu.html)
        var scr = Screen.FromControl(Bar).Bounds;
        AudioMenu.Bounds = new Rectangle(Math.Clamp(Bar.Left + x, scr.Left, scr.Right - w), Bar.Top - m - h, w, h);
        AudioMenu.Show(); AudioMenu.Activate(); ForceForeground(AudioMenu.Handle);
        if (lastAudio != null) AudioMenu.Send(lastAudio);
        AudioMenu.Send(new { type = "show" });
        audioMenuOpen = true;
        Host.Log("déroulant Audio Linker ouvert");
    }

    void HideAudioMenu()
    {
        if (!audioMenuOpen) return;
        audioMenuOpen = false; audioMenuHiddenAt = DateTime.UtcNow;
        AudioMenu.Hide();
        Host.Log("liste des setups Audio Linker fermée");
    }

    public void OnAudioMenuDeactivated() { if (audioMenuOpen) HideAudioMenu(); }


    void ShowStart()
    {
        if (centerOpen) HideCenter();
        int w = (int)(600 * scale), h = (int)(640 * scale), m = (int)(6 * scale);
        Start.Bounds = new Rectangle(Bar.Left + m, Bar.Top - m - h, w, h);
        Start.Show(); Start.Activate(); ForceForeground(Start.Handle);
        Start.Send(new { type = "show" });
        startOpen = true; Bar.Send(new { type = "start", open = true });
        Host.Log("menu Démarrer ouvert");
    }

    void HideStart()
    {
        if (!startOpen) return;
        startOpen = false; startHiddenAt = DateTime.UtcNow;
        Start.Hide(); Bar.Send(new { type = "start", open = false });
        Host.Log("menu Démarrer fermé");
    }

    public void OnStartDeactivated() { if (startOpen) HideStart(); }

    // L'Explorateur, le navigateur et Dépôts sont des programmes à part (cdx-explorer.exe, cdx-browser.exe, cdx-repos.exe, posés à côté de celui-ci) : un plantage de l'un
    // n'emporte ni la barre ni l'autre. Déjà lancés, ils reçoivent l'argument et ouvrent une fenêtre ou un onglet.
    void StartApp(string exe, string? arg)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, exe)) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            if (pinnedTheme) { psi.ArgumentList.Add("--theme"); psi.ArgumentList.Add(theme); }
            if (!string.IsNullOrEmpty(arg)) psi.ArgumentList.Add(arg);
            Process.Start(psi);
            Host.Log("lancé : " + exe + (arg != null ? " " + arg : ""));
        }
        catch (Exception ex) { Host.Log($"lancement de {exe} échoué : {ex.Message}"); }
    }

    void Launch(string target)
    {
        // un programme de la suite (SuiteApps.cs) : lancé à côté du shell, avec son argument s'il en accepte un
        if (SuiteApps.ByTarget(target) is { } app)
        {
            string exe = app.Exe + ".exe";
            // Audio Linker (C++) : posé dans bin\ par build.ps1, sinon là où il s'est inscrit ; déjà lancé, il passe devant (son instance unique)
            if (!app.DotNet && !File.Exists(Path.Combine(AppContext.BaseDirectory, exe))) exe = Suite.AppPath(app.Name) ?? exe;
            StartApp(exe, SuiteApps.ArgumentOf(app, target)); return;
        }
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); Host.Log("lancé : " + target); }
        catch (Exception ex) { Host.Log($"lancement de {target} échoué : {ex.Message}"); }
    }

    void Activate(IntPtr h)
    {
        if (!N.IsWindow(h)) return;
        Host.Log("activation : " + N.TitleOf(h));
        if (N.IsIconic(h)) ForceForeground(h);
        else if (h == lastFg) N.ShowWindow(h, 6 /* SW_MINIMIZE */);   // clic sur la fenêtre déjà active : on la réduit, comme la barre de Windows
        else ForceForeground(h);
    }

    static void ForceForeground(IntPtr h) => Host.ForceForeground(h);

}
