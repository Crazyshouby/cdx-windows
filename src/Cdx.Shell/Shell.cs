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

class Shell : IHostApp
{
    public readonly BarForm Bar;
    public readonly StartForm Start;
    public readonly SwitcherForm Switch;
    public readonly CenterForm Center;
    public readonly AudioMenuForm AudioMenu;
    public readonly BarMenuForm BarMenu;   // le menu contextuel d'un bouton de la barre (épingles, fenêtres), ouvert au-dessus de la barre
    public uint CallbackMsg;
    readonly PinStore pinStore;
    bool barMenuOpen; Action<string> barMenuAct;

    string theme; readonly string root;
    readonly bool pinnedTheme;   // un thème donné en argument : la suite ne le change pas
    IDisposable suiteWatch;
    CoreWebView2Environment env;
    double scale = 1; int barH = 36;
    IntPtr lastFg;
    readonly Dictionary<IntPtr, int> order = new(); int seq;
    LowLevelHook kbdHook;   // sur un fil à lui : la barre occupée ne doit jamais faire ramer le clavier de tout Windows
    bool startOpen, centerOpen, cleaned; DateTime startHiddenAt = DateTime.MinValue, centerHiddenAt = DateTime.MinValue;
    long pIdle, pKernel, pUser; int tick;
    bool audioMenuOpen; DateTime audioMenuHiddenAt = DateTime.MinValue; volatile object lastAudio;
    AudioLink audio;   // la liaison avec Audio Linker (tube « cdx.audio_linker »), si ce programme tourne
    TrayRelay tray;    // la zone de notification : les icônes des applis, reçues à la place de la barre de Windows

    public Shell(string theme)
    {
        pinnedTheme = theme != null; this.theme = theme ?? Suite.ReadAccent(); root = Host.FindWebRoot();
        Bar = new BarForm(this); Start = new StartForm(this); Switch = new SwitcherForm(this); Center = new CenterForm(this); AudioMenu = new AudioMenuForm(this); BarMenu = new BarMenuForm(this);
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
        Host.StartPilotPipe("shell", Bar, HandleControl, () => cleaned, "state", "notifs", "pins", "audio", "tray");   // Pilote lit l'état de la barre ; jamais launch ni evalbar

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
        Suite.RegisterApp("shell", Environment.ProcessPath);
        // Les deux autres programmes sont posés à côté du shell : leur chemin est enregistré même s'ils n'ont pas encore tourné (ou si apps.json garde un ancien dossier).
        foreach (var other in new[] { "explorer", "browser", "repos", "transfers", "notes", "youtube", "audio_linker" }) { var exe = Path.Combine(AppContext.BaseDirectory, "cdx-" + other + ".exe"); if (File.Exists(exe)) Suite.RegisterApp(other, exe); }
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
        var scr = Screen.PrimaryScreen.Bounds;
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
            string cmd = r.GetProperty("cmd").GetString();
            string Str(string k) => r.TryGetProperty(k, out var v) ? v.GetString() : null;
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
                case "alttab.pick": CommitSwitcher(new IntPtr(long.Parse(Str("h")))); break;
                case "launch": Launch(Str("target")); break;
                case "activate": Activate(new IntPtr(long.Parse(Str("h")))); break;
                case "close": N.PostMessage(new IntPtr(long.Parse(Str("h"))), 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); break;
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
        string title = data.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        string text = data.TryGetProperty("text", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : "";
        string level = data.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : "info";
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
    void StartApp(string exe, string arg)
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
        if (target.StartsWith("cdx:navigateur")) { StartApp("cdx-browser.exe", target.Contains('?') ? Uri.UnescapeDataString(target[(target.IndexOf('?') + 1)..]) : null); return; }
        if (target.StartsWith("cdx:repos")) { StartApp("cdx-repos.exe", null); return; }
        if (target.StartsWith("cdx:agenda")) { StartApp("cdx-agenda.exe", null); return; }
        if (target.StartsWith("cdx:courrier")) { StartApp("cdx-mail.exe", null); return; }
        if (target.StartsWith("cdx:pilote")) { StartApp("cdx-pilote.exe", null); return; }
        if (target.StartsWith("cdx:youtube")) { StartApp("cdx-youtube.exe", null); return; }
        // Audio Linker (C++, src/cdx-audio_linker) : posé dans bin\ par build.ps1 ; déjà lancé, il passe devant (son instance unique)
        if (target.StartsWith("cdx:audio_linker")) { StartApp(File.Exists(Path.Combine(AppContext.BaseDirectory, "cdx-audio_linker.exe")) ? "cdx-audio_linker.exe" : Suite.AppPath("audio_linker") ?? "cdx-audio_linker.exe", null); return; }
        if (target.StartsWith("cdx:terminal")) { StartApp("cdx-terminal.exe", target.Contains('?') ? Uri.UnescapeDataString(target[(target.IndexOf('?') + 1)..]) : null); return; }
        if (target.StartsWith("cdx:notes")) { StartApp("cdx-notes.exe", target.Contains('?') ? Uri.UnescapeDataString(target[(target.IndexOf('?') + 1)..]) : null); return; }
        if (target.StartsWith("cdx:explorer")) { StartApp("cdx-explorer.exe", target.Contains('?') ? Uri.UnescapeDataString(target[(target.IndexOf('?') + 1)..]) : null); return; }
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
                active = g.Many ? g.Active : r.Active, min = g.Many ? g.Min : r.Min, count = g.Windows.Count, g = g.Many ? g.Key : (string)null
            };
        }).ToList();
        Bar.Send(new { type = "tasks", items });
        if (++tick % 4 == 0) SendStats();
    }

    // Les fenêtres de la barre des tâches, dans l'ordre où elles sont apparues.
    List<TaskWin> TaskWins()
    {
        var found = new List<IntPtr>();
        N.EnumWindows((h, _) => { if (IsTaskWindow(h)) found.Add(h); return true; }, IntPtr.Zero);
        foreach (var h in found) if (!order.ContainsKey(h)) order[h] = ++seq;
        foreach (var dead in order.Keys.Where(k => !found.Contains(k)).ToList()) order.Remove(dead);
        return found.OrderBy(h => order[h]).Select(h => new TaskWin(h, N.ExePathOf(h) ?? "", N.TitleOf(h), h == lastFg && !N.IsIconic(h), N.IsIconic(h))).ToList();
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

