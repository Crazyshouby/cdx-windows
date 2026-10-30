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

class Shell : IHostApp
{
    public readonly BarForm Bar;
    public readonly StartForm Start;
    public readonly SwitcherForm Switch;
    public readonly CenterForm Center;
    public readonly AudioMenuForm AudioMenu;
    public uint CallbackMsg;

    string theme; readonly string root;
    readonly bool pinnedTheme;   // un thème donné en argument : la suite ne le change pas
    IDisposable suiteWatch;
    CoreWebView2Environment env;
    double scale = 1; int barH = 36;
    IntPtr lastFg;
    readonly Dictionary<IntPtr, int> order = new(); int seq;
    N.HookProc kbdProc; IntPtr kbdHook;   // le délégué est gardé en champ : le ramasse-miettes ne doit pas le libérer
    bool startOpen, centerOpen, cleaned; DateTime startHiddenAt = DateTime.MinValue, centerHiddenAt = DateTime.MinValue;
    long pIdle, pKernel, pUser; int tick;
    bool audioMenuOpen; DateTime audioMenuHiddenAt = DateTime.MinValue; volatile object lastAudio;
    AudioLink audio;   // la liaison avec AudioLinker (tube « kdx.audiolinker »), si ce programme tourne

    public Shell(string theme)
    {
        pinnedTheme = theme != null; this.theme = theme ?? Suite.ReadAccent(); root = Host.FindWebRoot();
        Bar = new BarForm(this); Start = new StartForm(this); Switch = new SwitcherForm(this); Center = new CenterForm(this); AudioMenu = new AudioMenuForm(this);
        Bar.Load += async (s, e) => { try { await InitAsync(); } catch (Exception ex) { Host.Log("initialisation échouée : " + ex); } };
    }

    async Task InitAsync()
    {
        scale = Bar.DeviceDpi / 96.0; barH = (int)Math.Round(36 * scale);
        CallbackMsg = N.RegisterWindowMessage("KdxShellAppBar");
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

        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (s, e) => Tick();
        timer.Start();
        Bus.Listen("shell", OnBus);
        Suite.RegisterApp("shell", Environment.ProcessPath);
        // Les deux autres programmes sont posés à côté du shell : leur chemin est enregistré même s'ils n'ont pas encore tourné (ou si apps.json garde un ancien dossier).
        foreach (var other in new[] { "explorer", "browser", "repos" }) { var exe = Path.Combine(AppContext.BaseDirectory, "kdx-" + other + ".exe"); if (File.Exists(exe)) Suite.RegisterApp(other, exe); }
        if (!pinnedTheme) suiteWatch = Suite.Watch(theme, a => { theme = a; foreach (var f in new HostForm[] { Bar, Start, Center, Switch, AudioMenu }) f.Send(new { type = "theme", value = a }); Host.Log("accent de la suite : " + a); });
        Host.Log($"shell prêt : thème {theme}, échelle {scale}, racine {root}");
    }

    void PlaceBar(ref APPBARDATA abd)
    {
        var scr = Screen.PrimaryScreen.Bounds;
        abd.uEdge = N.ABE_BOTTOM;
        abd.rc = new RECT { Left = scr.Left, Right = scr.Right, Top = scr.Bottom - barH, Bottom = scr.Bottom };
        N.SHAppBarMessage(N.ABM_QUERYPOS, ref abd);
        abd.rc.Top = abd.rc.Bottom - barH;
        N.SHAppBarMessage(N.ABM_SETPOS, ref abd);
        Bar.Bounds = Rectangle.FromLTRB(abd.rc.Left, abd.rc.Top, abd.rc.Right, abd.rc.Bottom);
        Host.Log($"barre placée : {Bar.Bounds}");
    }

    // ---------- crochet clavier : touche Windows, Win+E, Alt+Tab ----------
    // Windows ouvre son menu Démarrer quand la touche Windows est relâchée sans qu'aucune autre touche n'ait été vue entre-temps, et son
    // sélecteur quand il voit Alt+Tab. Pour les remplacer, on retire nos touches du flux, puis on glisse une touche neutre (VK 0xE8, non
    // attribuée) avant le relâchement de Windows ou d'Alt : sans elle, Windows croirait à un simple appui et ouvrirait son menu.
    // Fusible : un fichier %LOCALAPPDATA%\KdxShell\sans-clavier.txt empêche l'installation du crochet.
    bool winDown, winOther, winHandled, altDown, altSwallowed, switcherOpen;
    List<IntPtr> swItems = new(); int swSel;

    void InstallKeyboardHook()
    {
        if (File.Exists(Path.Combine(Host.DataDir, "sans-clavier.txt"))) { Host.Log("crochet clavier désactivé par sans-clavier.txt"); return; }
        kbdProc = KeyHook;
        kbdHook = N.SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, kbdProc, N.GetModuleHandle(null), 0);
        Host.Log(kbdHook == IntPtr.Zero ? $"crochet clavier refusé (erreur {Marshal.GetLastWin32Error()})" : "crochet clavier installé");
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
                            if (tap) Bar.BeginInvoke(new Action(() => { Host.Log("touche Windows : menu Démarrer KDX"); if (startOpen) HideStart(); else ShowStart(); }));
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
                            if (vk == 0x45 /* E */) { winHandled = true; Bar.BeginInvoke(new Action(() => { Host.Log("Win+E : Explorateur KDX"); Launch("kdx:explorer"); })); return (IntPtr)1; }
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
        return N.CallNextHookEx(kbdHook, code, wParam, lParam);
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
        try { var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = Bar.Handle }; N.SHAppBarMessage(N.ABM_REMOVE, ref abd); } catch { }
        if (kbdHook != IntPtr.Zero) { N.UnhookWindowsHookEx(kbdHook); kbdHook = IntPtr.Zero; }
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
                    if (Str("page") == "bar") { Tick(); SendStats(); audio?.Refresh(); PushNotifs(); }
                    else if (Str("page") == "audiomenu") { if (lastAudio != null) from.Send(lastAudio); }
                    else { from.Send(new { type = "user", name = Environment.UserName }); from.Send(new { type = "apps", items = ScanApps() }); }
                    break;
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
                case "nativeTray": RevealNativeTray(); break;
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
        var n = new { title, text, level, from, at = DateTimeOffset.Now.ToUnixTimeSeconds() };
        notifications.Add(n); if (notifications.Count > 50) notifications.RemoveAt(0);
        if (!centerOpen) unreadNotifs++;
        Host.Log($"notification de {from} : {title}");
        PushNotifs();
        return true;
    }

    async Task<string> HandleControl(string line)
    {
        int sp = line.IndexOf(' '); string c = sp < 0 ? line : line[..sp], a = sp < 0 ? "" : line[(sp + 1)..];
        switch (c)
        {
            case "launch": Launch(a); return "ok";
            case "evalbar": return await Bar.Web.CoreWebView2.ExecuteScriptAsync(a);   // exécute du JavaScript dans la page de la barre (tests)
            case "notifs": return notifications.Count + "/" + unreadNotifs;
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

    // Les setups d'AudioLinker, dans une petite fenêtre posée au-dessus de la barre, alignée sur le bord gauche du bouton (x : pixels de la page de la barre).
    void ShowAudioMenu(int x)
    {
        if (startOpen) HideStart(); if (centerOpen) HideCenter();
        int rows = 1; try { using var d = JsonDocument.Parse(JsonSerializer.Serialize(lastAudio)); if (d.RootElement.TryGetProperty("setups", out var s)) rows = Math.Clamp(s.GetArrayLength(), 1, 14); } catch { }
        int w = (int)(280 * scale), h = (int)((21 + 12 + 4 + rows * 27) * scale), m = (int)(6 * scale);
        var scr = Screen.FromControl(Bar).Bounds;
        AudioMenu.Bounds = new Rectangle(Math.Clamp(Bar.Left + x, scr.Left, scr.Right - w), Bar.Top - m - h, w, h);
        AudioMenu.Show(); AudioMenu.Activate(); ForceForeground(AudioMenu.Handle);
        if (lastAudio != null) AudioMenu.Send(lastAudio);
        AudioMenu.Send(new { type = "show" });
        audioMenuOpen = true;
        Host.Log("liste des setups AudioLinker ouverte");
    }

    void HideAudioMenu()
    {
        if (!audioMenuOpen) return;
        audioMenuOpen = false; audioMenuHiddenAt = DateTime.UtcNow;
        AudioMenu.Hide();
        Host.Log("liste des setups AudioLinker fermée");
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

    // L'Explorateur, le navigateur et Dépôts sont des programmes à part (kdx-explorer.exe, kdx-browser.exe, kdx-repos.exe, posés à côté de celui-ci) : un plantage de l'un
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
        if (target.StartsWith("kdx:navigateur")) { StartApp("kdx-browser.exe", target.Contains('?') ? Uri.UnescapeDataString(target[(target.IndexOf('?') + 1)..]) : null); return; }
        if (target.StartsWith("kdx:repos")) { StartApp("kdx-repos.exe", null); return; }
        if (target.StartsWith("kdx:explorer")) { StartApp("kdx-explorer.exe", target.Contains('?') ? Uri.UnescapeDataString(target[(target.IndexOf('?') + 1)..]) : null); return; }
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
        if (!N.IsWindowVisible(h) || h == Bar.Handle || h == Start.Handle || h == Switch.Handle || h == Center.Handle || h == AudioMenu.Handle) return false;
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

    // La zone de notification de Windows (icônes de Sunshine, TeamViewer…) n'a pas d'équivalent dans le shell : le bouton ▲ de la
    // barre remontre celle de Windows 8 secondes, par-dessus la nôtre.
    DateTime nativeUntil = DateTime.MinValue;

    void RevealNativeTray()
    {
        nativeUntil = DateTime.UtcNow.AddSeconds(8);
        var t = N.FindWindow("Shell_TrayWnd", null);
        if (t != IntPtr.Zero) N.SetWindowPos(t, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040 /* SWP_NOSIZE|NOMOVE|NOACTIVATE|SHOWWINDOW */);
    }

    void Tick()
    {
        if (Bar.Web.CoreWebView2 == null) return;
        if (DateTime.UtcNow >= nativeUntil)
        {
            bool wasShown = nativeUntil != DateTime.MinValue; nativeUntil = DateTime.MinValue;
            TaskbarState.HideWindow();
            if (wasShown) { Bar.TopMost = false; Bar.TopMost = true; }   // se remettre au-dessus
        }
        var fg = N.GetAncestor(N.GetForegroundWindow(), 2);
        if (fg != IntPtr.Zero && fg != Bar.Handle && fg != Start.Handle && fg != Switch.Handle && fg != Center.Handle && fg != AudioMenu.Handle) lastFg = fg;   // « active » = la dernière fenêtre qui n'est pas du shell

        var found = new List<IntPtr>();
        N.EnumWindows((h, _) => { if (IsTaskWindow(h)) found.Add(h); return true; }, IntPtr.Zero);
        foreach (var h in found) if (!order.ContainsKey(h)) order[h] = ++seq;
        foreach (var dead in order.Keys.Where(k => !found.Contains(k)).ToList()) order.Remove(dead);

        var items = found.OrderBy(h => order[h]).Select(h => new
        {
            h = h.ToInt64().ToString(), title = N.TitleOf(h), exe = N.ExePathOf(h) ?? "",
            active = h == lastFg && !N.IsIconic(h), min = N.IsIconic(h)
        }).ToList();
        Bar.Send(new { type = "tasks", items });
        if (++tick % 4 == 0) SendStats();
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
