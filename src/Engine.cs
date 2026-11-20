using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Threading;

namespace WarpSwitchNext
{
    sealed class StateInfo
    {
        public string Phase = "off";
        public string Message = "点击开启以连接";
        public string Sni = "—";
        public string Ip = "—";
        public string Colo = "—";
        public string Route = "—";
        /// <summary>usque's own last error, shown in full by 错误详情.</summary>
        public string Detail;
        public StateInfo Clone() => (StateInfo)MemberwiseClone();
    }

    /// <summary>A way to reach WARP: straight to the account's entry, or through the local relay to another entry.</summary>
    sealed class Route
    {
        public const string DefaultEntry = "162.159.198.2";
        /// <summary>The backup HTTP/2 entries the Mac app uses.</summary>
        public static readonly string[] Entries =
        {
            "162.159.198.3", "162.159.198.4", "162.159.198.5", "162.159.198.6",
            "162.159.198.7", "162.159.198.8", "162.159.199.3", "162.159.199.4",
        };
        public static Route Direct => new Route(null, false);

        public string Endpoint { get; set; }
        public bool Fragment { get; set; }
        public Route() { }
        public Route(string endpoint, bool fragment) { Endpoint = endpoint; Fragment = fragment; }

        public bool Valid => Endpoint == null || Entries.Contains(Endpoint);
        public bool Same(Route o) => o != null && o.Endpoint == Endpoint && o.Fragment == Fragment;
        public string Label => Endpoint == null ? "默认入口 " + DefaultEntry : "备用入口 " + Endpoint + (Fragment ? " · 握手分片" : "");
    }

    /// <summary>
    /// One connection, the same way as the reference project (warp-masque-bypass):
    ///   usque  -c account.json socks -b 127.0.0.1 -p 1080 --http2 -s &lt;伪装域名&gt;
    ///   sing-box: mixed (HTTP + SOCKS5) proxy on 127.0.0.1:7890 → usque, private addresses direct
    /// usque talks HTTP/2 over TCP 443 straight to Cloudflare and shows the 伪装域名 in the TLS
    /// handshake instead of consumer-masque.cloudflareclient.com. Runs on a background thread.
    /// </summary>
    sealed class Engine
    {
        readonly string dir, usque, singBox;
        readonly bool testOnly;
        readonly SystemProxy proxy;
        readonly ManualResetEvent cancelled = new ManualResetEvent(false);
        readonly AutoResetEvent poke = new AutoResetEvent(false);
        readonly object stateLock = new object();
        readonly StateInfo state = new StateInfo();
        readonly LogFile log, usqueLog, singLog;
        string sni;
        volatile string pendingSni;
        volatile bool resumed;
        Process usqueProcess, singProcess;
        HelloRelay relay;
        Route route = Route.Direct;

        public event Action<StateInfo> StateChanged;

        public Engine(string dataDirectory, string usquePath, string singBoxPath, string sni, bool testOnly)
        {
            dir = dataDirectory; usque = usquePath; singBox = singBoxPath; this.sni = sni; this.testOnly = testOnly;
            proxy = new SystemProxy(dir);
            log = new LogFile(Path.Combine(dir, "worker.log"));
            usqueLog = new LogFile(Path.Combine(dir, "usque.log"), cap: 5_000_000);
            singLog = new LogFile(Path.Combine(dir, "sing-box.log"), cap: 5_000_000);
        }

        string AccountPath => Path.Combine(dir, "account.json");
        string RuntimePath => Path.Combine(dir, "runtime.json");
        string RoutePath => Path.Combine(dir, "last-route.json");
        string ConfigPath => Path.Combine(dir, "sing-box.json");

        public bool Stopped => cancelled.WaitOne(0);
        public void Cancel() { cancelled.Set(); poke.Set(); }
        /// <summary>Use a new 伪装域名: only usque restarts; the proxy stays on.</summary>
        public void ChangeSni(string value) { pendingSni = value; poke.Set(); }
        /// <summary>Windows woke from sleep: the old tunnel is likely dead.</summary>
        public void NotifyResume() { resumed = true; poke.Set(); }

        public StateInfo Snapshot() { lock (stateLock) return state.Clone(); }

        void Publish(string phase, string message)
        {
            StateInfo copy;
            bool same;
            lock (stateLock) { same = state.Phase == phase && state.Message == message; state.Phase = phase; state.Message = message; copy = state.Clone(); }
            if (!same) log.Line("[" + phase + "] " + message);
            StateChanged?.Invoke(copy);
        }

        bool Pause(double seconds) => cancelled.WaitOne(TimeSpan.FromSeconds(seconds));

        /// <summary>The sing-box configuration of the reference project.</summary>
        public static string SingBoxConfig() => @"{
  ""log"": { ""level"": ""warn"", ""timestamp"": true },
  ""inbounds"": [
    { ""type"": ""mixed"", ""tag"": ""in"", ""listen"": ""127.0.0.1"", ""listen_port"": " + Ports.Proxy + @" }
  ],
  ""outbounds"": [
    { ""type"": ""socks"", ""tag"": ""warp-masque"", ""server"": ""127.0.0.1"", ""server_port"": " + Ports.Socks + @", ""version"": ""5"" },
    { ""type"": ""direct"", ""tag"": ""direct"" }
  ],
  ""route"": {
    ""rules"": [
      { ""action"": ""sniff"" },
      { ""ip_is_private"": true, ""outbound"": ""direct"" }
    ],
    ""final"": ""warp-masque""
  }
}
";

        public static string UsqueArguments(string configPath, int port, string sni, int relayPort = 0) =>
            "-c " + Util.Quote(configPath) + " socks -b 127.0.0.1 -p " + port + " --http2" +
            (relayPort > 0 ? " -P " + relayPort : "") + " -s " + sni + " --always-reconnect";

        /// <summary>
        /// Writes the config usque runs with for a route. The direct route uses the account as it is
        /// (entry 162.159.198.2, as in the reference project). Other routes point usque at the local
        /// relay, which connects to the chosen entry and may split the ClientHello, as the Mac app does.
        /// </summary>
        public static string RouteConfig(string accountPath, string runtimePath, Route r)
        {
            if (r.Endpoint == null) return accountPath;
            var account = Json.ReadObject(accountPath) ?? throw new UserError("WARP 账号文件损坏，请重新注册。");
            account["endpoint_h2_v4"] = "127.0.0.1";
            Json.Write(runtimePath, account);
            return runtimePath;
        }

        /// <summary>Starts a helper with no console window, logging its output; Windows ends it with this app.</summary>
        public static Process Launch(string file, string arguments, string workingDirectory, LogFile output)
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(file, arguments)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = workingDirectory,
                },
            };
            if (output != null)
            {
                p.OutputDataReceived += (s, e) => output.Line(e.Data);
                p.ErrorDataReceived += (s, e) => output.Line(e.Data);
            }
            try { p.Start(); }
            catch (Exception e)
            {
                throw new UserError("无法启动 " + Path.GetFileName(file) + "：" + e.Message + "。请确认 core 文件夹完整，且没有被杀毒软件隔离。");
            }
            ChildJob.Add(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        static void Kill(ref Process p)
        {
            var x = p; p = null;
            if (x == null) return;
            try { if (!x.HasExited) { x.Kill(); x.WaitForExit(3000); } } catch { }
            try { x.Dispose(); } catch { }
        }

        static bool Alive(Process p) { try { return p != null && !p.HasExited; } catch { return false; } }

        void StopRelay() { try { relay?.Stop(); } catch { } relay = null; }

        void StartUsque()
        {
            Kill(ref usqueProcess);
            StopRelay();
            usqueLog.Line("---- usque, 伪装域名 " + sni + ", " + route.Label + " ----");
            int relayPort = 0;
            if (route.Endpoint != null)
            {
                relay = new HelloRelay(route.Endpoint, route.Fragment, Ports.Relay);
                relay.Start();
                relayPort = Ports.Relay;
            }
            var config = RouteConfig(AccountPath, RuntimePath, route);
            usqueProcess = Launch(usque, UsqueArguments(config, Ports.Socks, sni, relayPort), dir, usqueLog);
            lock (stateLock) { state.Sni = sni; state.Route = route.Label; state.Ip = "—"; state.Colo = "—"; }
        }

        /// <summary>
        /// Every way to reach WARP, in the order the Mac app tries them: the last one that worked,
        /// the reference project's direct route, then the backup entries with the ClientHello split,
        /// then the backup entries unchanged.
        /// </summary>
        List<Route> Routes()
        {
            var own = Json.ReadObject(AccountPath);
            string ownEntry = own != null && own.TryGetValue("endpoint_h2_v4", out var v) && v is string e && e.Length > 0 ? e : Route.DefaultEntry;
            var entries = Route.Entries.Where(x => x != ownEntry).ToList();
            var list = new List<Route> { Route.Direct };
            list.AddRange(entries.Select(x => new Route(x, true)));
            list.AddRange(entries.Select(x => new Route(x, false)));
            var cached = Json.Read<Route>(RoutePath);
            if (cached != null && cached.Valid)
            {
                list.RemoveAll(x => x.Same(cached));
                list.Insert(0, cached);
            }
            return list;
        }

        /// <summary>Tries the routes one by one until traffic arrives over WARP. False if none works, we are stopped, or the 伪装域名 changed.</summary>
        bool Search(string phase, bool currentFirst)
        {
            var routes = Routes();
            if (currentFirst) { routes.RemoveAll(x => x.Same(route)); routes.Insert(0, route); }
            for (int i = 0; i < routes.Count; i++)
            {
                if (Stopped || pendingSni != null) return false;
                route = routes[i];
                Publish(phase, "正在尝试连接方式 " + (i + 1) + "/" + routes.Count + "：" + route.Label + "（伪装域名 " + sni + "）");
                StartUsque();
                if (WaitForWarp(route.Endpoint == null ? 20 : 15, restartDeadUsque: false))
                {
                    try { Json.Write(RoutePath, route); } catch { }
                    return true;
                }
                lastError = LastUsqueError(usqueLog.FilePath) ?? lastError;
            }
            return false;
        }

        string lastError;

        void StartSingBox()
        {
            Kill(ref singProcess);
            File.WriteAllText(ConfigPath, SingBoxConfig());
            singProcess = Launch(singBox, "run -c " + Util.Quote(ConfigPath) + " --disable-color", dir, singLog);
        }

        /// <summary>Asks Cloudflare, through 127.0.0.1:7890, whether the request arrived over WARP.</summary>
        public static Dictionary<string, string> Trace(IWebProxy via = null, int timeoutMs = 6000)
        {
            var values = new Dictionary<string, string>();
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("https://www.cloudflare.com/cdn-cgi/trace");
                request.Proxy = via ?? new WebProxy("127.0.0.1", Ports.Proxy);
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.KeepAlive = false;
                request.UserAgent = "WarpSwitchNext";
                request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    foreach (var line in reader.ReadToEnd().Split('\n'))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
            }
            catch { }
            return values;
        }

        public static bool IsWarp(Dictionary<string, string> t) => t.TryGetValue("warp", out var w) && (w == "on" || w == "plus");

        /// <summary>The last error usque printed, shortened, so a failure can say why.</summary>
        public static string LastUsqueError(string logPath)
        {
            try
            {
                var lines = File.ReadAllLines(logPath);
                var hit = lines.Reverse().Take(60).FirstOrDefault(l =>
                    l.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    l.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit == null) return null;
                int at = hit.IndexOf("] ");
                var text = at > 0 ? hit.Substring(at + 2) : hit;
                return text.Length > 400 ? text.Substring(0, 400) + "…" : text;
            }
            catch { return null; }
        }

        /// <summary>Waits until traffic arrives over WARP; false if usque gives up, the time runs out, or we are stopped.</summary>
        bool WaitForWarp(int seconds, bool restartDeadUsque = true)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!Stopped && DateTime.UtcNow < until && pendingSni == null)
            {
                if (Pause(2)) return false;
                if (!Alive(singProcess)) StartSingBox();
                var t = Trace();
                if (IsWarp(t))
                {
                    lock (stateLock) { state.Ip = t.TryGetValue("ip", out var ip) ? ip : "—"; state.Colo = t.TryGetValue("colo", out var c) ? c : "—"; }
                    return true;
                }
                if (!Alive(usqueProcess))
                {
                    if (!restartDeadUsque) return false; // usque gave up on this route: try the next one
                    StartUsque();
                }
            }
            return false;
        }

        string FailureMessage()
        {
            var why = LastUsqueError(usqueLog.FilePath) ?? lastError;
            lock (stateLock) state.Detail = why;
            return "用伪装域名 " + sni + " 试遍了所有连接方式（默认入口、" + Route.Entries.Length + " 个备用入口、握手分片）都连不上。点「网络诊断」查原因，或换一个伪装域名。";
        }

        public int Run()
        {
            int exitCode = 0;
            try
            {
                if (!Util.ValidDomain(sni)) throw new UserError("请先填写有效的伪装域名，例如 www.cloudflare.com");
                if (!File.Exists(usque) || !File.Exists(singBox)) throw new UserError("缺少连接组件（core 文件夹里的 usque.exe 或 sing-box.exe）。请重新解压完整的程序文件夹。");
                if (!Account.Valid(AccountPath)) throw new UserError("未找到有效的 WARP 账号，请先点「同意条款并注册」。");
                foreach (var port in new[] { Ports.Proxy, Ports.Socks, Ports.Relay })
                    if (Util.PortBusy(port)) throw new UserError("本机端口 " + port + " 已被其他程序占用（常见是 Clash 等代理软件），请先关闭它再连接。");
                if (!testOnly) proxy.Restore();

                // Right after sign-in or wake the network is usually not up yet.
                if (!Util.Online())
                {
                    Publish("connecting", "等待网络连接…");
                    var wait = DateTime.UtcNow.AddSeconds(90);
                    while (!Stopped && !Util.Online() && DateTime.UtcNow < wait) Pause(1);
                    if (Stopped) return 0;
                }

                StartSingBox();
                while (!Search("connecting", currentFirst: false))
                {
                    if (Stopped) return 0;
                    if (pendingSni == null) { Publish("error", FailureMessage()); return 1; }
                    sni = pendingSni; pendingSni = null;
                }
                if (testOnly) { Publish("on", "测试通过：WARP 已验证，未修改系统代理"); return 0; }

                proxy.Enable();
                Publish("on", Connected());

                int failures = 0, restarts = 0;
                double lastAwake = Util.AwakeSeconds();
                var lastWall = DateTime.UtcNow;
                while (!Stopped)
                {
                    poke.WaitOne(TimeSpan.FromSeconds(5));
                    if (Stopped) break;

                    // A new 伪装域名 from the window: restart usque with it, keep everything else.
                    var next = pendingSni;
                    if (next != null)
                    {
                        pendingSni = null; sni = next;
                        if (Search("reconnecting", currentFirst: true)) { failures = 0; restarts = 0; Publish("on", Connected()); }
                        else if (!Stopped && pendingSni == null) { proxy.Restore(); Publish("error", FailureMessage() + "\n系统代理已恢复。"); return 1; }
                        continue;
                    }

                    // The awake clock stops while the PC sleeps; a gap means we just woke up.
                    double awake = Util.AwakeSeconds();
                    bool woke = resumed || (DateTime.UtcNow - lastWall).TotalSeconds - (awake - lastAwake) > 10;
                    resumed = false; lastWall = DateTime.UtcNow; lastAwake = awake;

                    if (!Alive(singProcess)) StartSingBox();
                    var t = Trace();
                    if (IsWarp(t) && Alive(usqueProcess))
                    {
                        failures = 0; restarts = 0;
                        lock (stateLock) { state.Ip = t.TryGetValue("ip", out var ip) ? ip : "—"; state.Colo = t.TryGetValue("colo", out var c) ? c : "—"; }
                        Publish("on", Connected());
                        continue;
                    }
                    if (!Util.Online())
                    {
                        Publish("reconnecting", "等待网络恢复，恢复后会自动重新连接…");
                        continue;
                    }
                    failures++;
                    if (!woke && failures < 3 && Alive(usqueProcess))
                    {
                        Publish("reconnecting", "连接中断，等待隧道恢复（" + failures + "/3）");
                        continue;
                    }
                    // After sleep, a dead usque, or a long drop: restart the same route first, then look for another.
                    Publish("reconnecting", "正在重新建立连接（" + route.Label + "）…");
                    StartUsque();
                    if (WaitForWarp(20)) { failures = 0; restarts = 0; Publish("on", Connected()); continue; }
                    if (Stopped) break;
                    if (++restarts >= 2)
                    {
                        if (Search("reconnecting", currentFirst: true)) { failures = 0; restarts = 0; Publish("on", Connected()); continue; }
                        if (Stopped || pendingSni != null) continue;
                        proxy.Restore();
                        Publish("error", FailureMessage() + "\n系统代理已恢复。");
                        return 1;
                    }
                }
            }
            catch (UserError e) { Publish("error", e.Message); exitCode = 1; }
            catch (Exception e) { log.Line(e.ToString()); Publish("error", e.Message); exitCode = 1; }
            finally
            {
                try { if (!testOnly) proxy.Restore(); }
                catch (Exception e) { Publish("error", e.Message); exitCode = 1; }
                Kill(ref usqueProcess);
                Kill(ref singProcess);
                StopRelay();
                Util.TryDelete(RuntimePath);
                if (Snapshot().Phase != "error") Publish("off", "已关闭，系统代理已恢复");
            }
            return exitCode;
        }

        string Connected() => "已连接 · " + route.Label + " · 浏览器和使用系统代理的程序都会经过 WARP";

        /// <summary>Ends the helpers right away (Windows is shutting down).</summary>
        public void KillNow()
        {
            Cancel();
            try { if (Alive(usqueProcess)) usqueProcess.Kill(); } catch { }
            try { if (Alive(singProcess)) singProcess.Kill(); } catch { }
        }
    }
}
