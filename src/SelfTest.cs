using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WarpSwitchNext
{
    /// <summary>Checks run by the build on a real Windows machine.</summary>
    static class SelfTest
    {
        sealed class Report
        {
            readonly string file;
            public int Failures;
            public Report(string file) { this.file = file; if (file != null) File.WriteAllText(file, ""); }
            public void Line(string text)
            {
                Console.WriteLine(text);
                if (file != null) File.AppendAllText(file, text + Environment.NewLine);
            }
            public void Check(bool ok, string what)
            {
                Line((ok ? "ok    " : "FAIL  ") + what);
                if (!ok) Failures++;
            }
            public int Done(string summary)
            {
                Line(Failures == 0 ? "PASS: " + summary : "FAILED: " + Failures + " check(s)");
                return Failures == 0 ? 0 : 1;
            }
        }

        // ---------- Unit checks: no network, no system changes ----------

        public static int Units(string outFile)
        {
            var r = new Report(outFile);
            try
            {
                // The connection is started exactly like the reference project.
                var args = Engine.UsqueArguments(@"C:\data\account.json", Ports.Socks, "www.cloudflare.com");
                r.Check(args == "-c \"C:\\data\\account.json\" socks -b 127.0.0.1 -p 1080 --http2 -s www.cloudflare.com --always-reconnect", "usque command (default entry): " + args);
                args = Engine.UsqueArguments(@"C:\data\runtime.json", Ports.Socks, "dock.schoology.com", Ports.Relay);
                r.Check(args.Contains("--http2 -P 18443 -s dock.schoology.com"), "usque command (backup entry through the relay): " + args);
                var hello = new byte[5 + 300]; hello[0] = 22; hello[1] = 3; hello[2] = 1; hello[3] = 1; hello[4] = 44; hello[5] = 1;
                var parts = ClientHello.Split(hello, hello.Length);
                r.Check(parts.Count == 7 && parts.Sum(x => x.Length - 5) == 300 && parts.All(x => x[0] == 22), "ClientHello split into 7 TLS records with the same bytes");
                r.Check(Route.Entries.Length == 8 && !Route.Entries.Contains(Route.DefaultEntry), "8 backup entries, as in the Mac app");
                var config = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(Engine.SingBoxConfig()) as Dictionary<string, object>;
                var inbound = ((object[])config["inbounds"])[0] as Dictionary<string, object>;
                var outbound = ((object[])config["outbounds"])[0] as Dictionary<string, object>;
                var route = config["route"] as Dictionary<string, object>;
                r.Check((string)inbound["type"] == "mixed" && (string)inbound["listen"] == "127.0.0.1" && (int)inbound["listen_port"] == Ports.Proxy && !inbound.ContainsKey("set_system_proxy"),
                        "sing-box listens on 127.0.0.1:" + Ports.Proxy + " (mixed); the app sets the system proxy itself");
                r.Check((string)outbound["type"] == "socks" && (int)outbound["server_port"] == Ports.Socks && (string)route["final"] == "warp-masque",
                        "sing-box sends everything to usque on port " + Ports.Socks + ", private addresses direct");
                var trace = Diagnostics.ParseTrace("fl=1\nip=104.28.1.2\ncolo=LAX\nwarp=on\n");
                r.Check(Engine.IsWarp(trace) && trace["colo"] == "LAX" && !Engine.IsWarp(Diagnostics.ParseTrace("warp=off")), "trace page parsed (warp=on / off)");
                r.Check(Defaults.Sni == "www.cloudflare.com" && Defaults.SniCandidates.Contains("cloudflareaccess.com"), "default 伪装域名 is www.cloudflare.com, as in the reference");
                r.Check(File.Exists(Paths.Usque) && File.Exists(Paths.SingBox), "core\\usque.exe and core\\sing-box.exe are next to the app");

                r.Check(Util.ValidDomain("www.bing.com") && Util.ValidDomain("dock.example.edu"), "valid domains accepted");
                r.Check(!Util.ValidDomain("evil; command") && !Util.ValidDomain("https://example.com") && !Util.ValidDomain("bing") && !Util.ValidDomain(""), "invalid domains rejected");
                var typed = new (string text, string expected)[]
                {
                    ("dock。schoology。com", "dock.schoology.com"),          // Chinese input method full stop
                    ("ｄｏｃｋ．ｓｃｈｏｏｌｏｇｙ．ｃｏｍ", "dock.schoology.com"), // full-width letters and dot
                    (" Dock.Schoology.com ", "dock.schoology.com"),
                    ("https://dock.schoology.com/home?x=1", "dock.schoology.com"), // pasted web address
                    ("www.bing.com.", "www.bing.com"),
                };
                foreach (var (text, expected) in typed)
                    r.Check(Util.NormalizeDomain(text) == expected && Util.ValidDomain(Util.NormalizeDomain(text)), "typed “" + text + "” becomes " + expected);
                r.Check(!Util.ValidDomain(Util.NormalizeDomain("dock schoology")) && !Util.ValidDomain(Util.NormalizeDomain("中文.com")), "text that isn't a domain is still rejected");

                var merged = SystemProxy.MergeBypass("*.corp.example;LOCALHOST");
                r.Check(merged.StartsWith("*.corp.example;LOCALHOST") && merged.Contains("192.168.*") && merged.Contains("172.31.*") && merged.Contains("<local>")
                        && merged.Split(';').Count(x => x.Equals("localhost", StringComparison.OrdinalIgnoreCase)) == 1, "proxy bypass keeps existing rules and adds local networks");
                r.Check(SystemProxy.IsOurs(" 127.0.0.1:7890 ") && !SystemProxy.IsOurs("127.0.0.1:8080") && !SystemProxy.IsOurs(null), "own proxy address recognised");

                var temp = Path.Combine(Path.GetTempPath(), "warp-switch-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try
                {
                    var settingsPath = Path.Combine(temp, "settings.json");
                    Json.Write(settingsPath, new Settings { Sni = "dock.example.edu", AutoConnect = true });
                    var back = Json.Read<Settings>(settingsPath);
                    r.Check(back != null && back.Sni == "dock.example.edu" && back.AutoConnect, "settings saved and read back");
                    File.WriteAllText(settingsPath, "{\"Sni\":\"www.bing.com\",\"Mode\":\"fragment\"}");
                    back = Json.Read<Settings>(settingsPath);
                    r.Check(back != null && back.Sni == "www.bing.com" && !back.AutoConnect, "settings files from version 1 still load");
                    var accountPath = Path.Combine(temp, "account.json");
                    File.WriteAllText(accountPath, "{\"private_key\":\"k\",\"id\":\"i\",\"access_token\":\"t\",\"endpoint_pub_key\":\"p\",\"ipv4\":\"172.16.0.2\",\"ipv6\":\"fd01::2\",\"license\":\"x\",\"n\":3}");
                    r.Check(Account.Valid(accountPath) && Account.Load(accountPath) != null, "complete account accepted");
                    File.WriteAllText(accountPath, "{\"private_key\":\"k\"}");
                    r.Check(!Account.Valid(accountPath), "incomplete account rejected");
                }
                finally { try { Directory.Delete(temp, true); } catch { } }

            }
            catch (Exception e) { r.Check(false, "unexpected error: " + e); }
            return r.Done("usque and sing-box set up like the reference, domain checks, proxy rules, settings and account files");
        }

        // ---------- System proxy: changes and restores the Windows proxy (build machine only) ----------

        static string RegistryProxy()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                return "ProxyEnable=" + key?.GetValue("ProxyEnable") + " ProxyServer=" + key?.GetValue("ProxyServer");
        }

        static string Describe(ProxyConfig c) => "flags=" + c.Flags + " server='" + c.Server + "' bypass='" + c.Bypass + "' pac='" + c.AutoConfigUrl + "'";

        public static int Proxy(string outFile)
        {
            var r = new Report(outFile);
            var dir = Path.Combine(Path.GetTempPath(), "warp-switch-proxy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var original = WinInet.Query();
            r.Line("info  machine proxy before test: " + Describe(original) + " | " + RegistryProxy());
            const int mask = WinInet.Direct | WinInet.Proxy | WinInet.AutoProxyUrl | WinInet.AutoDetect;
            try
            {
                var proxy = new SystemProxy(dir);
                // A typical laptop: automatic detection on, plus an old manual proxy that is switched off.
                var start = new ProxyConfig { Flags = WinInet.Direct | WinInet.AutoDetect, Server = "10.0.0.1:8080", Bypass = "*.corp.example", AutoConfigUrl = "http://wpad.example/proxy.pac" };
                WinInet.Apply(start);
                var seen = WinInet.Query();
                r.Check((seen.Flags & mask) == start.Flags && seen.Server == start.Server, "test settings applied: " + Describe(seen));

                proxy.Enable();
                var on = WinInet.Query();
                r.Check((on.Flags & mask) == (WinInet.Direct | WinInet.Proxy), "connected: manual proxy only (auto-detect and script paused): flags=" + on.Flags);
                r.Check(on.Server == Ports.ProxyAddress, "connected: proxy is " + Ports.ProxyAddress);
                r.Check(on.Bypass.StartsWith("*.corp.example") && on.Bypass.Contains("192.168.*"), "connected: bypass keeps old rules and adds local networks");
                var reg = RegistryProxy();
                r.Check(reg.Contains("ProxyEnable=1") && reg.Contains("ProxyServer=" + Ports.ProxyAddress), "connected: registry shows the proxy for other apps (" + reg + ")");
                r.Check(WebRequest.GetSystemWebProxy().GetProxy(new Uri("https://example.com/")).Authority == Ports.ProxyAddress, "connected: .NET system proxy resolves to " + Ports.ProxyAddress);
                r.Check(File.Exists(Path.Combine(dir, "proxy-snapshot.json")), "connected: previous settings saved to disk");

                proxy.Restore();
                var off = WinInet.Query();
                r.Check((off.Flags & mask) == start.Flags && off.Server == start.Server && off.Bypass == start.Bypass && off.AutoConfigUrl == start.AutoConfigUrl,
                        "disconnected: every previous setting restored: " + Describe(off));
                r.Check(!File.Exists(Path.Combine(dir, "proxy-snapshot.json")), "disconnected: saved copy removed");

                // Another proxy program is active: refuse, change nothing.
                var foreign = new ProxyConfig { Flags = WinInet.Direct | WinInet.Proxy, Server = "10.9.9.9:3128", Bypass = "<local>" };
                WinInet.Apply(foreign);
                bool refused = false;
                try { proxy.Enable(); } catch (UserError e) { refused = true; r.Line("info  refusal message: " + e.Message); }
                var still = WinInet.Query();
                r.Check(refused && still.Server == foreign.Server && !File.Exists(Path.Combine(dir, "proxy-snapshot.json")), "another active proxy is left alone");

                // The user changed the proxy while connected: restoring must not overwrite their choice.
                WinInet.Apply(start);
                proxy.Enable();
                var chosen = new ProxyConfig { Flags = WinInet.Direct | WinInet.Proxy, Server = "10.8.8.8:8888", Bypass = "" };
                WinInet.Apply(chosen);
                proxy.Restore();
                var kept = WinInet.Query();
                r.Check(kept.Server == chosen.Server && (kept.Flags & WinInet.Proxy) != 0, "a proxy the user picked while connected is kept");

                // App crashed while connected: the next start puts things back.
                WinInet.Apply(start);
                proxy.Enable();
                new SystemProxy(dir).Restore();
                var recovered = WinInet.Query();
                r.Check((recovered.Flags & mask) == start.Flags && recovered.Server == start.Server, "after a crash, the next start restores the proxy");
            }
            catch (Exception e) { r.Check(false, "unexpected error: " + e); }
            finally
            {
                try { WinInet.Apply(original); } catch { }
                try { Directory.Delete(dir, true); } catch { }
                r.Line("info  machine proxy after test: " + Describe(WinInet.Query()));
            }
            return r.Done("system proxy set, verified and restored");
        }

        // ---------- Real connection: registers a WARP account and connects (build machine only) ----------

        static bool Pump(Func<bool> done, int seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until) { Application.DoEvents(); if (done()) return true; Thread.Sleep(100); }
            return done();
        }

        static bool ViaSystemProxyIsWarp(out string detail)
        {
            var t = Engine.Trace(WebRequest.GetSystemWebProxy());
            detail = "warp=" + (t.TryGetValue("warp", out var w) ? w : "?") + ", ip=" + (t.TryGetValue("ip", out var ip) ? ip : "?") + ", colo=" + (t.TryGetValue("colo", out var c) ? c : "?");
            return Engine.IsWarp(t);
        }

        static bool Registered(Report r, string dataDir)
        {
            var account = Path.Combine(dataDir, "account.json");
            if (Account.Valid(account)) return true;
            var pending = Path.Combine(dataDir, "account.pending.json");
            var p = Process.Start(new ProcessStartInfo(Paths.Usque, "-c " + Util.Quote(pending) + " register -a")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dataDir,
            });
            var output = p.StandardOutput.ReadToEndAsync();
            var errors = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60000)) p.Kill();
            bool ok = p.ExitCode == 0 && Account.Valid(pending);
            r.Check(ok, "registered a new WARP account with usque.exe register -a");
            if (!ok) { r.Line("info  register output: " + (output.Result + errors.Result).Replace("\n", " | ").Trim()); return false; }
            File.Move(pending, account);
            return true;
        }

        public static int Connect(string dataDir, string outFile)
        {
            var r = new Report(outFile);
            dataDir = dataDir ?? Path.Combine(Path.GetTempPath(), "warp-switch-connect-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataDir);
            r.Line("info  data: " + dataDir);
            const int mask = WinInet.Direct | WinInet.Proxy | WinInet.AutoProxyUrl | WinInet.AutoDetect;
            try
            {
                if (!Registered(r, dataDir)) return r.Done("");

                // 1. usque + sing-box only, the system proxy untouched.
                var test = new Engine(dataDir, Paths.Usque, Paths.SingBox, "www.cloudflare.com", testOnly: true);
                StateInfo reached = null;
                test.StateChanged += s => { r.Line("      [" + s.Phase + "] " + s.Message); if (s.Phase == "on") reached = s; };
                int code = test.Run();
                r.Check(code == 0 && reached != null && reached.Ip != "—", "usque --http2 -s www.cloudflare.com + sing-box reached WARP: exit IP " + reached?.Ip + ", node " + reached?.Colo);

                // 1b. The Mac app's routes: a backup entry through the relay, with and without the ClientHello split.
                foreach (var forced in new[] { new Route("162.159.198.4", true), new Route("162.159.199.3", false) })
                {
                    Json.Write(Path.Combine(dataDir, "last-route.json"), forced);
                    var e = new Engine(dataDir, Paths.Usque, Paths.SingBox, "dock.schoology.com", testOnly: true);
                    StateInfo got = null;
                    e.StateChanged += s => { if (s.Phase == "on") got = s; };
                    int rc = e.Run();
                    r.Check(rc == 0 && got != null && got.Route == forced.Label, "connected through " + forced.Label + " (" + (got?.Route ?? "failed") + ", exit IP " + got?.Ip + ")");
                }
                Util.TryDelete(Path.Combine(dataDir, "last-route.json"));
                r.Check(!File.Exists(Path.Combine(dataDir, "runtime.json")), "runtime config removed after closing");

                // 2. As the app runs it: system proxy on, real traffic, change the 伪装域名 while connected, then off.
                var before = WinInet.Query();
                var engine = new Engine(dataDir, Paths.Usque, Paths.SingBox, "www.cloudflare.com", testOnly: false);
                string last = "";
                engine.StateChanged += s => { var line = "[" + s.Phase + "] " + s.Message; if (line != last) { last = line; r.Line("      " + line); } };
                int exit = -1;
                var thread = new Thread(() => exit = engine.Run()) { IsBackground = true };
                thread.Start();
                bool Wait(Func<StateInfo, bool> ok, int seconds)
                {
                    var until = DateTime.UtcNow.AddSeconds(seconds);
                    while (DateTime.UtcNow < until && thread.IsAlive) { if (ok(engine.Snapshot())) return true; Thread.Sleep(300); }
                    return ok(engine.Snapshot());
                }
                bool connected = Wait(s => s.Phase == "on", 90);
                r.Check(connected, "connected (已连接)");
                if (connected)
                {
                    var reg = RegistryProxy();
                    r.Check(reg.Contains("ProxyEnable=1") && reg.Contains("ProxyServer=" + Ports.ProxyAddress), "system proxy points at sing-box while connected (" + reg + ")");
                    r.Check(ViaSystemProxyIsWarp(out var d1), "a normal HTTPS request through the system proxy arrives via WARP (" + d1 + ")");

                    engine.ChangeSni("www.bing.com");
                    bool again = Wait(s => s.Phase == "on" && s.Sni == "www.bing.com", 90);
                    var usqueLog = File.Exists(Path.Combine(dataDir, "usque.log")) ? File.ReadAllText(Path.Combine(dataDir, "usque.log")) : "";
                    r.Check(again && usqueLog.Contains("伪装域名 www.bing.com"), "changed 伪装域名 to www.bing.com while connected: only usque restarted, connected again");
                    string d2 = "not reconnected";
                    r.Check(again && ViaSystemProxyIsWarp(out d2), "traffic still goes through WARP after the change (" + d2 + ")");
                    var still = RegistryProxy();
                    r.Check(still.Contains("ProxyServer=" + Ports.ProxyAddress), "system proxy stayed on during the change");
                }
                engine.Cancel();
                r.Check(thread.Join(30000), "closing finished within 30 seconds");
                var after = WinInet.Query();
                r.Check((after.Flags & mask) == (before.Flags & mask) && after.Server == before.Server && !SystemProxy.IsOurs(after.Server),
                        "system proxy restored after closing: " + Describe(after));

                // 3. The app's controller, with the domain typed through a Chinese input method.
                var c = new AppController(dataDir, Paths.Usque, Paths.SingBox);
                c.Settings.Sni = "www.cloudflare.com";
                string seen = "";
                c.Changed += () => { var line = "[" + c.State.Phase + "] " + c.State.Message; if (line != seen) { seen = line; r.Line("      " + line); } };
                c.UserStart();
                r.Check(Pump(() => c.State.Phase == "on", 90), "app connected with www.cloudflare.com");
                var saved = c.ApplySni("dock。schoology。com");
                r.Check(saved == "dock.schoology.com" && c.Settings.Sni == "dock.schoology.com" && c.Working, "applied 伪装域名 while connected (typed with a Chinese full stop); the connection stays on");
                bool switched = Pump(() => c.State.Phase == "on" && c.State.Sni == "dock.schoology.com", 90);
                r.Check(switched, "app reconnected with dock.schoology.com by itself");
                if (switched) r.Check(ViaSystemProxyIsWarp(out var d3), "traffic goes through WARP with dock.schoology.com (" + d3 + ")");
                c.UserStop();
                r.Check(Pump(() => !c.Working, 30), "closed");
                var end = WinInet.Query();
                r.Check(!((end.Flags & WinInet.Proxy) != 0 && SystemProxy.IsOurs(end.Server)), "system proxy restored at the end: " + Describe(end));
                var saveFile = Json.Read<Settings>(Path.Combine(dataDir, "settings.json"));
                r.Check(saveFile?.Sni == "dock.schoology.com", "the new 伪装域名 is saved for next time");
                // Leave the default for the UI test that runs next.
                c.Settings.Sni = Defaults.Sni; c.SaveSettings();
            }
            catch (Exception e) { r.Check(false, "unexpected error: " + e); }
            return r.Done("registered, connected through usque --http2 + sing-box, used the system proxy, changed the 伪装域名 while connected, and restored the proxy");
        }

        // ---------- 网络诊断 on the build machine (its network does not block WARP) ----------

        public static int Diagnose(string dataDir, string outFile)
        {
            var r = new Report(outFile);
            try
            {
                dataDir = dataDir ?? Path.Combine(Path.GetTempPath(), "warp-switch-diag-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dataDir);
                if (!Registered(r, dataDir)) return r.Done("");
                var started = DateTime.UtcNow;
                var report = new Diagnostics(dataDir, Paths.Usque, "dock.schoology.com", line => r.Line("    | " + line)).Run();
                r.Line("info  took " + (int)(DateTime.UtcNow - started).TotalSeconds + " s");
                r.Check(report.DirectOk, "direct HTTPS check works");
                r.Check(report.MasqueTcpOk, "TCP 443 to the WARP entry is open");
                r.Check(report.CloudflareSniOk, "TLS handshake with SNI www.cloudflare.com completes");
                r.Check(report.Snis.Count == 4 + 2 * Defaults.SniCandidates.Count(x => x != "dock.schoology.com"), "tested the current 伪装域名 on 4 routes and every candidate on 2 (" + report.Snis.Count + ")");
                r.Check(report.Snis.Any(s => s.Works && s.Route.Fragment), "a backup entry with the ClientHello split works through the relay");
                r.Check(report.Snis.Any(s => s.Sni == "www.cloudflare.com" && s.Works && s.Route.Endpoint == null), "usque really connected with www.cloudflare.com on the default entry");
                r.Check(report.BestSni != null && report.Summary.Length > 0, "a working 伪装域名 is suggested: " + report.Summary);
                r.Check(File.Exists(Path.Combine(dataDir, "diagnose.txt")), "diagnose.txt saved");
                r.Check(!Directory.GetFiles(dataDir, "diagnose-*").Any() && !Process.GetProcessesByName("usque").Any(), "temporary usque processes and logs cleaned up");
            }
            catch (Exception e) { r.Check(false, "unexpected error: " + e); }
            return r.Done("网络诊断 checked the network and found a working 伪装域名 with real usque connections");
        }

        // ---------- Screenshots of the window in each state ----------

        /// <summary>Text that does not fit its control, or controls on top of each other: what broke on a 250 % screen.</summary>
        static IEnumerable<string> LayoutProblems(Form form)
        {
            var all = new List<Control>();
            void Walk(Control p) { foreach (Control c in p.Controls) { if (c.Visible) { all.Add(c); if (!(c is TextBoxBase)) Walk(c); } } }
            Walk(form);
            foreach (var c in all)
            {
                // One-line values end in "…" by design; the status message must fit completely.
                if (c is Label label && !(c is LinkLabel) && !string.IsNullOrEmpty(label.Text) && (!label.AutoEllipsis || label.Name == "MessageLabel"))
                {
                    var need = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, int.MaxValue), TextFormatFlags.WordBreak);
                    if (need.Height > label.Height + 1 || need.Width > label.Width + 1)
                        yield return "text does not fit in '" + label.Text + "': needs " + need.Width + "x" + need.Height + ", has " + label.Width + "x" + label.Height;
                }
                if (c is TextBox box && box.Height < box.Font.Height + 2)
                    yield return "text box shorter than its text: " + box.Height + " < " + box.Font.Height;
                if (c is Button button)
                {
                    var need = TextRenderer.MeasureText(button.Text, button.Font);
                    if (need.Height > button.Height || need.Width > button.Width) yield return "button text does not fit: " + button.Text;
                }
            }
            foreach (var c in all.OfType<Label>().Where(l => l.AutoEllipsis && l.Name != "MessageLabel" && !string.IsNullOrEmpty(l.Text)))
            {
                var need = TextRenderer.MeasureText(c.Text, c.Font);
                if (need.Height > c.Height + 1) yield return "text taller than its row: '" + c.Text + "'";
            }
            foreach (var a in all)
                foreach (var b in all)
                    if (a != b && a.Parent == b.Parent && string.CompareOrdinal(a.Name + a.GetHashCode(), b.Name + b.GetHashCode()) < 0 && a.Bounds.IntersectsWith(b.Bounds))
                        yield return "controls overlap: '" + (a.Name.Length > 0 ? a.Name : a.Text) + "' and '" + (b.Name.Length > 0 ? b.Name : b.Text) + "'";
        }

        public static int Screenshots(string folder, string outFile)
        {
            var r = new Report(outFile);
            // Report drawing errors instead of showing an error dialog that nobody can close.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => r.Check(false, "UI error: " + e.Exception);
            folder = folder ?? "screenshots";
            Directory.CreateDirectory(folder);
            var data = Path.Combine(Path.GetTempPath(), "warp-switch-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(data);
            try
            {
                var controller = new AppController(data, Paths.Usque, Paths.SingBox);
                using (var form = new MainForm(controller))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(40, 40);
                    form.ShowInTaskbar = false;
                    form.Show();
                    Application.DoEvents();
                    var scenes = new (string name, StateInfo state, bool setup, bool working, bool auto)[]
                    {
                        ("1-first-run", new StateInfo(), true, false, false),
                        ("2-registering", new StateInfo { Phase = "registering", Message = "正在创建你的独立免费 WARP 账号…" }, true, true, false),
                        ("3-connecting", new StateInfo { Phase = "connecting", Message = "正在尝试连接方式 3/17：备用入口 162.159.198.4 · 握手分片（伪装域名 dock.schoology.com）", Sni = "dock.schoology.com" }, false, true, true),
                        ("4-connected", new StateInfo { Phase = "on", Message = "已连接 · 备用入口 162.159.198.4 · 握手分片", Sni = "dock.schoology.com", Ip = "2a09:bac5:9f23:2828::400:3d", Colo = "IAD", Route = "备用入口 162.159.198.4 · 握手分片" }, false, true, true),
                        ("5-error", new StateInfo { Phase = "error", Message = "用 dock.schoology.com 试了全部 17 种连接方式都连不上。点「网络诊断」查原因。", Detail = "2026/11/19 13:10:07 EST Failed to connect tunnel: failed to dial connect-ip over HTTP/2: connect-ip: failed to send request: Connect \"https://162.159.198.2/\": read tcp 10.0.0.5:51234->162.159.198.2:443: wsarecv: An existing connection was forcibly closed by the remote host." }, false, false, true),
                        ("6-off", new StateInfo { Message = "已关闭，系统代理已恢复" }, false, false, false),
                    };
                    foreach (var scene in scenes)
                    {
                        controller.Preview(scene.state, scene.setup, scene.working, scene.auto);
                        form.Refresh();
                        Application.DoEvents();
                        using (var bmp = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                            var file = Path.Combine(folder, scene.name + ".png");
                            bmp.Save(file, ImageFormat.Png);
                            r.Check(File.Exists(file), "saved " + file + " (" + form.Width + "x" + form.Height + ", scale " + Ui.Scale + ")");
                        }
                        foreach (var problem in LayoutProblems(form)) r.Check(false, scene.name + ": " + problem);
                    }
                    using (var tray = new Bitmap(4 * 40, 40))
                    using (var g = Graphics.FromImage(tray))
                    {
                        g.Clear(Color.FromArgb(32, 32, 32));
                        int x = 4;
                        foreach (var phase in new[] { "off", "connecting", "on", "error" })
                        {
                            g.DrawIcon(Ui.TrayIcon(phase), new Rectangle(x, 4, 32, 32));
                            x += 40;
                        }
                        tray.Save(Path.Combine(folder, "7-tray-icons.png"), ImageFormat.Png);
                    }
                    r.Check(StatusRing.LastError == null, "status picture drawn without errors" + (StatusRing.LastError == null ? "" : ": " + StatusRing.LastError));
                    form.Hide();
                }
            }
            catch (Exception e) { r.Check(false, "unexpected error: " + e); }
            finally { try { Directory.Delete(data, true); } catch { } }
            return r.Done("window rendered in every state");
        }
    }
}
