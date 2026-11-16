using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WarpSwitchNext
{
    /// <summary>
    /// Everything the window and tray show and do. Lives on the UI thread; the connection itself
    /// runs in an Engine (usque + sing-box) on a background thread.
    /// </summary>
    sealed class AppController
    {
        public event Action Changed;

        public readonly string DataDir;
        readonly string usque, singBox;
        readonly SynchronizationContext ui;
        readonly System.Windows.Forms.Timer retryTimer = new System.Windows.Forms.Timer();

        public StateInfo State = new StateInfo();
        public Settings Settings;
        public bool Working, Stopping, NeedsSetup, Registering;
        /// <summary>Set when the user presses 关闭, so auto-connect never reconnects against their wish.</summary>
        public bool UserStopped;

        Engine worker;
        Process registration;
        LogFile registrationLog;
        bool registrationCancelled;
        Action afterStop;
        double retryDelay = 30;

        string SettingsPath => Path.Combine(DataDir, "settings.json");
        string AccountPath => Path.Combine(DataDir, "account.json");
        string PendingPath => Path.Combine(DataDir, "account.pending.json");

        public AppController(string dataDirectory, string usquePath, string singBoxPath)
        {
            DataDir = dataDirectory;
            usque = usquePath;
            singBox = singBoxPath;
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            Directory.CreateDirectory(DataDir);
            Settings = Json.Read<Settings>(SettingsPath) ?? new Settings();
            if (!Util.ValidDomain(Settings.Sni)) Settings.Sni = Defaults.Sni;
            NeedsSetup = !Account.Valid(AccountPath);
            // Put the proxy back if the app or PC stopped while connected.
            try { new SystemProxy(DataDir).Restore(); }
            catch (Exception e) { State.Phase = "error"; State.Message = e.Message; }
            AutoStart.Sync(Settings.AutoConnect);

            retryTimer.Tick += (s, e) =>
            {
                retryTimer.Stop();
                if (ShouldAutoConnect && !Working && !Stopping) Start();
            };
            // Lid opened / PC woke up: reconnect if the connection is no longer running.
            SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode == PowerModes.Resume) Post(() => { worker?.NotifyResume(); AutoResume(3); });
            };
            // Network came back (joined Wi-Fi): reconnect if the connection is no longer running.
            NetworkChange.NetworkAvailabilityChanged += (s, e) =>
            {
                if (e.IsAvailable) Post(() => AutoResume(2));
            };
            // Windows is signing out or shutting down: put the proxy back before the app is killed.
            SystemEvents.SessionEnding += (s, e) => EmergencyRestore();
        }

        void Post(Action action) => ui.Post(_ => action(), null);
        void Notify() => Changed?.Invoke();

        public bool AutoConnect => Settings.AutoConnect;
        bool ShouldAutoConnect => AutoConnect && !UserStopped && !NeedsSetup;
        public bool Busy => State.Phase == "connecting" || State.Phase == "reconnecting" || State.Phase == "stopping" || State.Phase == "registering";

        public string Title
        {
            get
            {
                switch (State.Phase)
                {
                    case "registering": return "正在注册";
                    case "on": return "已连接";
                    case "connecting": return "正在连接";
                    case "reconnecting": return "正在重连";
                    case "stopping": return "正在关闭";
                    case "error": return "暂未连接";
                    default: return "已关闭";
                }
            }
        }

        public void SaveSettings()
        {
            try { Json.Write(SettingsPath, Settings); } catch { }
        }

        /// <summary>Start connecting on our own, unless the user turned it off or it is already running.</summary>
        public void AutoResume(double seconds)
        {
            if (!ShouldAutoConnect) return;
            retryTimer.Stop();
            retryTimer.Interval = Math.Max(1, (int)(seconds * 1000));
            retryTimer.Start();
        }

        public void UserStart() { UserStopped = false; retryDelay = 30; Start(); }
        public string SingBoxPath => singBox;
        public string UsquePath => usque;
        public void UserStop() { UserStopped = true; retryTimer.Stop(); Stop(null); }

        public void Retry()
        {
            UserStopped = false; retryTimer.Stop();
            if (Working) Stop(() => Start()); else Start();
        }

        /// <summary>
        /// Saves a new 伪装域名, at any time. While connecting or connected only usque restarts with it
        /// (the proxy stays on); while registering it is used once the account is ready.
        /// Returns the domain as saved, or a UserError explaining what is wrong.
        /// </summary>
        public string ApplySni(string input)
        {
            var value = Util.NormalizeDomain(input);
            if (!Util.ValidDomain(value))
                throw new UserError("“" + (input ?? "").Trim() + "”不是有效的域名。请用英文字母输入，例如 www.cloudflare.com");
            bool changed = value != Settings.Sni;
            Settings.Sni = value; SaveSettings();
            if (changed && worker != null && !Registering && !Stopping)
            {
                worker.ChangeSni(value);
                State = State.Clone();
                State.Phase = "reconnecting"; State.Message = "正在用新的伪装域名 " + value + " 重新连接…";
            }
            Notify();
            return value;
        }

        public bool Diagnosing;

        /// <summary>Runs 网络诊断 on a background thread; lines arrive on the UI thread.</summary>
        public void Diagnose(Action<string> line, Action<Diagnostics.Report> done)
        {
            if (Diagnosing) return;
            Diagnosing = true; Notify();
            var sni = Settings.Sni;
            new Thread(() =>
            {
                Diagnostics.Report report = null;
                try { report = new Diagnostics(DataDir, usque, sni, l => Post(() => line(l))).Run(); }
                catch (Exception e) { Post(() => line("诊断出错：" + e.Message)); }
                Post(() => { Diagnosing = false; Notify(); done(report); });
            }) { IsBackground = true, Name = "Diagnostics" }.Start();
        }

        public void SetAutoConnect(bool on)
        {
            try { AutoStart.Set(on); }
            catch (Exception e)
            {
                MessageBox.Show("无法修改开机启动设置：" + e.Message, "WARP 新开关", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Notify();
                return;
            }
            Settings.AutoConnect = on;
            SaveSettings();
            if (!on) retryTimer.Stop();
            Notify();
        }

        public void Start()
        {
            if (Working || Stopping) return;
            var sni = Util.NormalizeDomain(Settings.Sni);
            if (!Util.ValidDomain(sni)) { State = new StateInfo { Phase = "error", Message = "请先填写有效的伪装域名，例如 www.cloudflare.com" }; Notify(); return; }
            Settings.Sni = sni; SaveSettings();
            if (NeedsSetup) { Register(); return; }

            var w = new Engine(DataDir, usque, singBox, sni, testOnly: false);
            w.StateChanged += s => Post(() =>
            {
                if (worker != w) return;
                State = s;
                if (s.Phase == "on") retryDelay = 30;
                Notify();
            });
            worker = w;
            Working = true;
            State = new StateInfo { Phase = "connecting", Message = "正在连接 WARP（伪装域名 " + sni + "）…", Sni = sni };
            new Thread(() =>
            {
                try { w.Run(); } catch { }
                Post(() => Completed(w));
            }) { IsBackground = true, Name = "Engine" }.Start();
            Notify();
        }

        void Completed(Engine w)
        {
            if (worker != w) return;
            State = w.Snapshot();
            worker = null; Working = false; Stopping = false;
            if (State.Phase != "error")
            {
                State = new StateInfo { Message = "已关闭，系统代理已恢复" };
            }
            else if (ShouldAutoConnect)
            {
                // Auto-connect is on and the user didn't turn it off: try again later, backing off.
                AutoResume(retryDelay);
                retryDelay = Math.Min(retryDelay * 2, 300);
            }
            Notify();
            RunAfterStop();
        }

        void RunAfterStop()
        {
            var after = afterStop; afterStop = null;
            after?.Invoke();
        }

        public void Stop(Action after)
        {
            if (!Working) { after?.Invoke(); return; }
            afterStop += after;
            if (Stopping) return;
            Stopping = true;
            State = State.Clone();
            State.Phase = "stopping"; State.Message = "正在关闭并恢复系统代理…";
            if (Registering)
            {
                registrationCancelled = true;
                try { if (registration != null && !registration.HasExited) registration.Kill(); } catch { }
            }
            else worker?.Cancel();
            Notify();
        }

        /// <summary>Stops everything before the app exits; calls back once the proxy is restored.</summary>
        public void Shutdown(Action done) => Stop(done);

        public void EmergencyRestore()
        {
            try { worker?.KillNow(); } catch { }
            try { if (registration != null && !registration.HasExited) registration.Kill(); } catch { }
            try { new SystemProxy(DataDir).Restore(); } catch { }
        }

        void Register()
        {
            if (!File.Exists(usque))
            {
                State = new StateInfo { Phase = "error", Message = "找不到连接组件（core 文件夹）。请重新解压完整的程序文件夹。" };
                Notify(); return;
            }
            registrationCancelled = false;
            Registering = true; Working = true;
            State = new StateInfo { Phase = "registering", Message = "正在创建你的独立免费 WARP 账号…" };
            Util.TryDelete(PendingPath);
            registrationLog = new LogFile(Path.Combine(DataDir, "registration.log"), truncate: true);
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(usque, "-c " + Util.Quote(PendingPath) + " register -a")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = DataDir,
                },
                EnableRaisingEvents = true,
            };
            var log = registrationLog;
            p.OutputDataReceived += (s, e) => log.Line(e.Data);
            p.ErrorDataReceived += (s, e) => log.Line(e.Data);
            p.Exited += (s, e) => Post(() => FinishRegistration(p));
            try
            {
                p.Start();
                ChildJob.Add(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                registration = p;
                new Thread(() =>
                {
                    Thread.Sleep(40000);
                    try { if (!p.HasExited) p.Kill(); } catch { }
                }) { IsBackground = true }.Start();
            }
            catch (Exception e)
            {
                Registering = false; Working = false; registration = null;
                State = new StateInfo { Phase = "error", Message = "无法启动注册：" + e.Message };
            }
            Notify();
        }

        void FinishRegistration(Process p)
        {
            if (registration != p) return;
            registration = null; Working = false; Stopping = false; Registering = false;
            int code = -1;
            try { p.WaitForExit(); code = p.ExitCode; } catch { }
            try
            {
                if (registrationCancelled) { State = new StateInfo { Message = "注册已取消" }; return; }
                if (code != 0 || !Account.Valid(PendingPath))
                {
                    State = new StateInfo { Phase = "error", Message = "注册未完成。请检查网络后重试，也可点“打开日志”查看原因。" };
                    return;
                }
                if (File.Exists(AccountPath))
                    File.Move(AccountPath, Path.Combine(DataDir, "account.invalid." + Guid.NewGuid().ToString("N") + ".json"));
                File.Move(PendingPath, AccountPath);
                NeedsSetup = false;
                Start();
            }
            catch (Exception e)
            {
                State = new StateInfo { Phase = "error", Message = "账号未能保存：" + e.Message };
            }
            finally
            {
                Util.TryDelete(PendingPath);
                Notify();
                RunAfterStop();
            }
        }

        /// <summary>Only for the screenshot test: show a given state without connecting.</summary>
        public void Preview(StateInfo state, bool needsSetup, bool working, bool autoConnect)
        {
            State = state; NeedsSetup = needsSetup; Working = working; Registering = state.Phase == "registering";
            Settings.AutoConnect = autoConnect;
            Notify();
        }
    }
}
