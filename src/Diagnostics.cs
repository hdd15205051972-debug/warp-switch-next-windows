using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WarpSwitchNext
{
    /// <summary>
    /// 网络诊断, the Windows version of the reference project's diagnose.sh and test-sni.sh:
    /// finds out whether the network blocks WARP by IP or by SNI, then really connects with usque
    /// once per candidate 伪装域名 to find one that works on this network.
    /// </summary>
    sealed class Diagnostics
    {
        public const string MasqueIp = "162.159.198.2";   // WARP MASQUE entry
        public const string WireGuardIp = "162.159.192.1"; // WARP WireGuard entry
        public const string DefaultSni = "consumer-masque.cloudflareclient.com";

        public sealed class SniResult
        {
            public string Sni;
            public bool Works;
            public string Detail;
        }

        public sealed class Report
        {
            public bool DirectOk, MasqueTcpOk, DefaultSniOk, CloudflareSniOk;
            public readonly List<SniResult> Snis = new List<SniResult>();
            public readonly StringBuilder Text = new StringBuilder();
            public string Summary = "";
            public string BestSni => Snis.FirstOrDefault(s => s.Works)?.Sni;
        }

        readonly string dir, usque, currentSni;
        readonly Action<string> progress;
        readonly Report report = new Report();

        public Diagnostics(string dataDirectory, string usquePath, string currentSni, Action<string> progress)
        {
            dir = dataDirectory; usque = usquePath; this.currentSni = currentSni; this.progress = progress;
        }

        void Say(string line)
        {
            report.Text.AppendLine(line);
            progress?.Invoke(line);
        }

        public Report Run()
        {
            Say("WARP 新开关 网络诊断  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            Say("");

            Say("1. 普通 HTTPS（TCP 443，不经过代理）");
            var direct = DirectTrace();
            report.DirectOk = direct != null;
            Say(report.DirectOk ? "   通：cloudflare.com 可访问" + (direct.TryGetValue("loc", out var loc) ? "（" + loc + "）" : "")
                                : "   不通：基础网络连不上 cloudflare.com");

            Say("2. 到 WARP 入口的 TCP 443");
            var entries = new List<string> { MasqueIp, WireGuardIp };
            var accountEndpoint = AccountEndpoint();
            if (accountEndpoint != null && !entries.Contains(accountEndpoint)) entries.Insert(0, accountEndpoint);
            foreach (var ip in entries)
            {
                bool ok = TcpOpen(ip, 443, 4000);
                if (ip == MasqueIp || ip == accountEndpoint) report.MasqueTcpOk |= ok;
                Say("   " + ip + ":443 " + (ok ? "通" : "不通（按 IP 封锁）") + (ip == accountEndpoint ? "  ← 你的账号用的入口" : ""));
            }

            Say("3. SNI 测试（对 WARP 入口 " + MasqueIp + " 做 TLS 握手）");
            var handshakeSnis = new List<string> { DefaultSni, "www.cloudflare.com" };
            if (Util.ValidDomain(currentSni) && !handshakeSnis.Contains(currentSni)) handshakeSnis.Add(currentSni);
            foreach (var sni in handshakeSnis)
            {
                var error = Handshake(MasqueIp, sni, 6000);
                // A TLS alert still came from Cloudflare's server, so the network let the SNI through.
                bool reached = error == null || error.StartsWith("服务器拒绝");
                if (sni == DefaultSni) report.DefaultSniOk = reached;
                if (sni == "www.cloudflare.com") report.CloudflareSniOk = reached;
                Say("   SNI=" + sni + (error == null ? "  握手成功" : reached ? "  服务器有回应（" + error + "），没被网络拦截" : "  握手失败/被重置（" + error + "）  ← 按 SNI 封锁"));
            }

            Say("4. 用 usque 实际连接 WARP（HTTP/2，每个伪装域名单独测试，最多约 20 秒）");
            var candidates = new List<string>();
            if (Util.ValidDomain(currentSni)) candidates.Add(currentSni);
            candidates.AddRange(Defaults.SniCandidates.Where(s => !candidates.Contains(s)));
            if (!File.Exists(usque)) Say("   跳过：缺少 core\\usque.exe");
            else if (!Account.Valid(Path.Combine(dir, "account.json"))) Say("   跳过：还没有 WARP 账号，请先点「同意条款并注册」");
            else
            {
                var rng = new Random();
                var tasks = candidates.Select((sni, i) =>
                {
                    int port = FreePort(20000 + rng.Next(0, 5000) + i * 7);
                    return Task.Run(() => TestSni(sni, port));
                }).ToArray();
                Task.WaitAll(tasks);
                foreach (var t in tasks)
                {
                    var r = t.Result;
                    report.Snis.Add(r);
                    Say("   " + (r.Works ? "✅ 可用  " : "❌ 失败  ") + r.Sni + (string.IsNullOrEmpty(r.Detail) ? "" : "  " + r.Detail));
                }
            }

            Say("");
            report.Summary = Interpret();
            Say("结论：" + report.Summary);
            try { File.WriteAllText(Path.Combine(dir, "diagnose.txt"), report.Text.ToString(), new UTF8Encoding(true)); } catch { }
            return report;
        }

        string Interpret()
        {
            var best = report.BestSni;
            if (!report.DirectOk)
                return "这台电脑现在连普通网站都打不开。先确认已连上 Wi-Fi，并在浏览器里完成校园网登录（认证页面），再重新诊断。";
            if (best != null)
            {
                var also = string.Join("、", report.Snis.Where(s => s.Works && s.Sni != best).Select(s => s.Sni));
                if (best == currentSni)
                    return "当前伪装域名 " + best + " 在这个网络上能连上 WARP，可以直接开启。" + (also.Length > 0 ? "其他可用：" + also + "。" : "");
                return "当前伪装域名 " + currentSni + " 连不上，但 " + best + " 可以。点「使用 " + best + "」即可换成它。" + (also.Length > 0 ? "其他可用：" + also + "。" : "");
            }
            if (!report.MasqueTcpOk)
                return "这个网络按 IP 封锁了 WARP 的入口（" + MasqueIp + " 的 TCP 443 不通）。换伪装域名对这种封锁没有用，请换一个网络（例如手机热点）。";
            if (!report.DefaultSniOk && report.CloudflareSniOk)
                return "这个网络按 SNI 封锁 WARP 的默认域名，这正是伪装域名能解决的情况，但测试的几个域名都没连上。可以在窗口里换一个常见网站的域名（例如 www.microsoft.com）再诊断，并把 diagnose.txt 发给开发者。";
            if (!report.CloudflareSniOk && !report.DefaultSniOk)
                return "到 WARP 入口的 TLS 握手无论用什么域名都会被重置，这个网络可能整体拦截了 Cloudflare 的这段地址或深度检测了流量。请换一个网络试试。";
            return "WARP 入口和握手都正常，但 usque 没能建立隧道。请看上面每个域名后面的错误信息，或点「打开日志」查看 usque.log。";
        }

        /// <summary>The trace page fetched directly, ignoring the system proxy; null if it can't be reached.</summary>
        static Dictionary<string, string> DirectTrace()
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("https://www.cloudflare.com/cdn-cgi/trace");
                request.Proxy = new WebProxy(); // no proxy
                request.Timeout = 8000; request.ReadWriteTimeout = 8000; request.KeepAlive = false;
                request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    return ParseTrace(reader.ReadToEnd());
            }
            catch { return null; }
        }

        public static Dictionary<string, string> ParseTrace(string text)
        {
            var values = new Dictionary<string, string>();
            foreach (var line in (text ?? "").Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return values;
        }

        string AccountEndpoint()
        {
            var values = Account.Load(Path.Combine(dir, "account.json"));
            if (values != null && values.TryGetValue("endpoint_v4", out var v) && v is string s && IPAddress.TryParse(s, out _)) return s;
            return null;
        }

        public static bool TcpOpen(string host, int port, int timeoutMs)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync(host, port);
                    return connect.Wait(timeoutMs) && client.Connected;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// A TLS handshake with the given SNI, the way curl in the reference project does it (SNI plus
        /// ALPN h2, TLS 1.3). .NET Framework cannot send ALPN, and WARP's entry refuses a handshake
        /// without it, so the ClientHello is written here. Returns null when the server answers with a
        /// ServerHello (nothing in the network stopped it), otherwise a short reason.
        /// </summary>
        public static string Handshake(string ip, string sni, int timeoutMs)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    if (!client.ConnectAsync(ip, 443).Wait(timeoutMs)) return "TCP 超时";
                    client.ReceiveTimeout = timeoutMs; client.SendTimeout = timeoutMs;
                    var s = client.GetStream();
                    var hello = ClientHello(sni);
                    s.Write(hello, 0, hello.Length);
                    var head = ReadExactly(s, 5);
                    var body = ReadExactly(s, Math.Min(head[3] * 256 + head[4], 16384));
                    if (head[0] == 22 && body.Length > 0 && body[0] == 2) return null;  // ServerHello
                    if (head[0] == 21) return "服务器拒绝（TLS 警报 " + (body.Length > 1 ? body[1].ToString() : "?") + "）";
                    return "意外的回应";
                }
            }
            catch (AggregateException e) { return Short(e.InnerException ?? e); }
            catch (Exception e) { return Short(e); }
        }

        /// <summary>A TLS 1.3 ClientHello (also offering 1.2) with SNI and ALPN h2 / http/1.1.</summary>
        public static byte[] ClientHello(string sni)
        {
            var rng = new Random();
            byte[] Random(int n) { var b = new byte[n]; rng.NextBytes(b); return b; }
            List<byte> U16(int v) => new List<byte> { (byte)(v >> 8), (byte)v };
            List<byte> Ext(int type, List<byte> data) { var e = U16(type); e.AddRange(U16(data.Count)); e.AddRange(data); return e; }
            List<byte> Vec16(List<byte> data) { var v = U16(data.Count); v.AddRange(data); return v; }

            var name = Encoding.ASCII.GetBytes(sni);
            var serverName = new List<byte> { 0 }; serverName.AddRange(U16(name.Length)); serverName.AddRange(name);
            var alpn = new List<byte>();
            foreach (var proto in new[] { "h2", "http/1.1" }) { alpn.Add((byte)proto.Length); alpn.AddRange(Encoding.ASCII.GetBytes(proto)); }
            var keyShare = U16(0x001d); keyShare.AddRange(U16(32)); keyShare.AddRange(Random(32));

            var ext = new List<byte>();
            ext.AddRange(Ext(0x0000, Vec16(serverName)));                                         // server_name
            ext.AddRange(Ext(0x000a, Vec16(new List<byte> { 0, 0x1d, 0, 0x17, 0, 0x18 })));       // supported_groups
            ext.AddRange(Ext(0x000b, new List<byte> { 1, 0 }));                                    // ec_point_formats
            ext.AddRange(Ext(0x000d, Vec16(new List<byte> { 4, 3, 8, 4, 4, 1, 5, 3, 8, 5, 5, 1, 8, 6, 6, 1 }))); // signature_algorithms
            ext.AddRange(Ext(0x0010, Vec16(alpn)));                                                // ALPN
            ext.AddRange(Ext(0x002b, new List<byte> { 4, 3, 4, 3, 3 }));                           // supported_versions 1.3, 1.2
            ext.AddRange(Ext(0x002d, new List<byte> { 1, 1 }));                                    // psk_key_exchange_modes
            ext.AddRange(Ext(0x0033, Vec16(keyShare)));                                            // key_share x25519

            var hello = new List<byte> { 3, 3 };
            hello.AddRange(Random(32));
            hello.Add(32); hello.AddRange(Random(32));                                             // session id
            hello.AddRange(Vec16(new List<byte> { 0x13, 1, 0x13, 2, 0x13, 3, 0xc0, 0x2b, 0xc0, 0x2f, 0xc0, 0x2c, 0xc0, 0x30, 0xcc, 0xa9, 0xcc, 0xa8 }));
            hello.Add(1); hello.Add(0);                                                            // compression: none
            hello.AddRange(Vec16(ext));

            var handshake = new List<byte> { 1, 0, (byte)(hello.Count >> 8), (byte)hello.Count };
            handshake.AddRange(hello);
            var record = new List<byte> { 22, 3, 1 };
            record.AddRange(U16(handshake.Count));
            record.AddRange(handshake);
            return record.ToArray();
        }

        static string Short(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            if (e is SocketException se)
            {
                if (se.SocketErrorCode == SocketError.ConnectionReset) return "连接被重置";
                if (se.SocketErrorCode == SocketError.TimedOut) return "超时";
                return se.SocketErrorCode.ToString();
            }
            if (e is IOException) return "连接被中断";
            var m = e.Message;
            return m.Length > 60 ? m.Substring(0, 60) + "…" : m;
        }

        static int FreePort(int preferred)
        {
            for (int p = preferred; p < preferred + 200; p++)
                if (!Util.PortBusy(p)) return p;
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            return port;
        }

        /// <summary>test-sni.sh: a temporary usque with this SNI, then a trace through it (socks5h).</summary>
        SniResult TestSni(string sni, int port)
        {
            var result = new SniResult { Sni = sni };
            var logPath = Path.Combine(dir, "diagnose-usque-" + port + ".log");
            var log = new LogFile(logPath, truncate: true);
            Process p = null;
            try
            {
                p = Engine.Launch(usque, Engine.UsqueArguments(Path.Combine(dir, "account.json"), port, sni), dir, log);
                var until = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < until)
                {
                    Thread.Sleep(2000);
                    var trace = SocksTrace(port, 6000);
                    if (trace != null && Engine.IsWarp(trace))
                    {
                        result.Works = true;
                        result.Detail = "warp=" + trace["warp"] + (trace.TryGetValue("colo", out var colo) ? " 节点=" + colo : "");
                        return result;
                    }
                    bool exited; try { exited = p.HasExited; } catch { exited = true; }
                    if (exited) break;
                }
                var why = Engine.LastUsqueError(logPath);
                result.Detail = why ?? "20 秒内没有连上";
                return result;
            }
            catch (Exception e) { result.Detail = e.Message; return result; }
            finally
            {
                try { if (p != null && !p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch { }
                try { p?.Dispose(); } catch { }
                Thread.Sleep(200);
                Util.TryDelete(logPath);
            }
        }

        /// <summary>GET https://www.cloudflare.com/cdn-cgi/trace through a SOCKS5 proxy, resolving the name remotely.</summary>
        public static Dictionary<string, string> SocksTrace(int socksPort, int timeoutMs, string host = "www.cloudflare.com")
        {
            try
            {
                using (var client = new TcpClient())
                {
                    if (!client.ConnectAsync("127.0.0.1", socksPort).Wait(timeoutMs)) return null;
                    client.ReceiveTimeout = timeoutMs; client.SendTimeout = timeoutMs;
                    var s = client.GetStream();
                    s.Write(new byte[] { 5, 1, 0 }, 0, 3);
                    var hello = ReadExactly(s, 2);
                    if (hello[0] != 5 || hello[1] != 0) return null;
                    var name = Encoding.ASCII.GetBytes(host);
                    var req = new List<byte> { 5, 1, 0, 3, (byte)name.Length };
                    req.AddRange(name); req.Add(443 >> 8); req.Add(443 & 0xff);
                    s.Write(req.ToArray(), 0, req.Count);
                    var head = ReadExactly(s, 4);
                    if (head[1] != 0) return null;
                    int skip = head[3] == 1 ? 4 : head[3] == 4 ? 16 : ReadExactly(s, 1)[0];
                    ReadExactly(s, skip + 2);
                    using (var ssl = new SslStream(s, false))
                    {
                        ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12, false);
                        var get = Encoding.ASCII.GetBytes("GET /cdn-cgi/trace HTTP/1.1\r\nHost: " + host + "\r\nUser-Agent: WarpSwitchNext\r\nConnection: close\r\n\r\n");
                        ssl.Write(get, 0, get.Length);
                        var text = new StreamReader(ssl, Encoding.ASCII).ReadToEnd();
                        int body = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        return ParseTrace(body >= 0 ? text.Substring(body + 4) : text);
                    }
                }
            }
            catch { return null; }
        }

        static byte[] ReadExactly(Stream s, int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buffer, read, count - read);
                if (n <= 0) throw new IOException("closed");
                read += n;
            }
            return buffer;
        }
    }
}
