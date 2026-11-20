# WARP 新开关 · Windows 版

用 Cloudflare WARP 的 **MASQUE over HTTP/2 + 伪装域名（SNI）** 绕过按 SNI 封锁 WARP 的校园网 / 公司网。一键开启或关闭，伪装域名随时可改，内置「网络诊断」帮你查出网络封了什么、哪个伪装域名能用。

连接方式与 [EdmundMad0309/warp-masque-bypass](https://github.com/EdmundMad0309/warp-masque-bypass)（macOS）相同，这是它的 Windows 图形界面版：

```
浏览器 → 系统代理 127.0.0.1:7890 (sing-box) → 127.0.0.1:1080 (usque) ══ HTTP/2, TCP 443, SNI=伪装域名 ══▶ Cloudflare WARP
```

- `usque -c account.json socks -b 127.0.0.1 -p 1080 --http2 -s <伪装域名>`：走 TCP 443 的 HTTP/2，TLS 握手里显示伪装域名，而不是会被封的 `consumer-masque.cloudflareclient.com`。
- `sing-box`：在 `127.0.0.1:7890` 提供 HTTP + SOCKS5 代理，转发给 usque，内网地址直连。
- 默认入口连不上时，自动改用 Mac 版在学校网络上验证过的方式：通过本机 `127.0.0.1:18443` 的转发连到 8 个备用入口之一（`162.159.198.3`–`.8`、`162.159.199.3`、`.4`），可选把 TLS ClientHello 拆成多个小记录（握手分片）。连上的方式会被记住。
- 程序把 Windows 系统代理指向 `127.0.0.1:7890`，关闭时原样恢复。

**适用于 64 位 Windows 10（1803 或更新）和 Windows 11**（ARM 电脑通过系统自带的 x64 兼容运行）。使用 Windows 自带的 .NET Framework，无需另装运行库。此项目为非官方客户端，与 Cloudflare 无隶属关系。

macOS 版是另一个独立项目：[warp-switch-next](https://github.com/hdd15205051972-debug/warp-switch-next)。

## 下载和安装

从 **[Releases 下载最新版](https://github.com/hdd15205051972-debug/warp-switch-next-windows/releases/latest)** 中下载 `WARP-Switch-Next-Windows-2.1.0.zip`。

1. 解压，把「WARP 新开关」文件夹放到一个固定的位置（例如「文档」或 `D:\Apps`）。`core` 文件夹必须和 exe 放在一起。
2. 双击 `WARP 新开关.exe`。程序没有代码签名，Windows 可能提示「Windows 已保护你的电脑」：点「更多信息」→「仍要运行」。
3. 首次点击「同意条款并注册」，程序会向 Cloudflare 注册属于你自己的免费 WARP 账号，然后连接。
4. 看到「已连接」后，Edge、Chrome 等浏览器和使用系统代理的程序即可使用。

从 1.x 升级：直接用新文件夹替换旧的，账号会保留。

## 日常使用

- **开启 / 关闭**：依次尝试默认入口、备用入口、握手分片，直到流量真的经过 WARP（`warp=on`）才启用系统代理；关闭时恢复原来的代理设置。用的是哪种方式显示在「连接方式」一栏。
- **伪装域名**：窗口中间的输入框，默认 `www.cloudflare.com`。改完点「应用」或按回车，**任何时候都能改**；连接中应用只会重启 usque，系统代理不断开。中文输入法打出的「。」和全角字母会自动纠正，粘贴整个网址会只取域名。
- **网络诊断**：在学校网络上连不上时点它。会依次检查：普通 HTTPS、到 WARP 入口 `162.159.198.2` / `162.159.192.1` 的 TCP 443、用默认域名和伪装域名对入口做 TLS 握手，最后用 usque 真实连接：当前伪装域名分别走默认入口、备用入口（拆分 / 不拆分握手），`www.cloudflare.com`、`cloudflareaccess.com`、`www.bing.com` 各走默认入口和备用入口。最后给出中文结论，找到能用的域名时可以一键换上。结果保存在 `diagnose.txt`，可以复制发给别人。
- **开机时自动连接**：登录 Windows 时程序在通知区域后台启动并自动连接；睡眠唤醒、网络恢复后自动重连。手动点「关闭」后不会自动重连。
- **通知区域图标**：猫猫图标右下角的小圆点表示状态（灰色已关闭、蓝色连接中、绿色已连接、橙色未连上）。关掉窗口后程序仍在后台运行；「退出」会先关闭连接并恢复代理。
- **打开日志**：数据文件夹里的 `usque.log`（隧道）、`sing-box.log`（本机代理）、`worker.log`（程序）。

## 诊断结果怎么看

| 诊断结果 | 说明 | 怎么办 |
| --- | --- | --- |
| 默认域名握手被重置，`www.cloudflare.com` 握手成功 | 按 SNI 封锁，本程序正好能解决 | 用诊断里标 ✅ 的伪装域名 |
| `162.159.198.2:443` TCP 不通 | 按 IP 封锁了 WARP 入口 | 换伪装域名没用，换网络（如手机热点） |
| 所有域名握手都被重置 | 整段地址被拦或深度检测 | 换网络 |
| 普通 HTTPS 不通 | 还没联网或没登录校园网认证页 | 先在浏览器里登录校园网 |

## 系统代理如何设置

连接时，程序把当前用户的 Windows 代理设为 `127.0.0.1:7890`，保留原有的绕过规则，并让本地和常用内网地址直连。「自动检测设置」和「使用设置脚本」在连接期间暂时关闭，关闭连接时原样恢复。

- 修改前先把原设置保存到磁盘。程序意外退出或断电后，下次打开会自动恢复；Windows 注销或关机时也会先恢复。
- 系统已经在用其他代理软件时，程序会拒绝连接并提示，不会覆盖。端口 7890 或 1080 被占用（常见是 Clash）时也会提示。
- 不遵循系统代理的程序（部分游戏、微软商店应用等）不经过 WARP。

## 隐私和服务器验证

安装包不含共享账号、私钥、令牌或个人配置，每位使用者单独注册自己的账号。账号和日志仅保存在本机 `%LOCALAPPDATA%\WarpSwitchNext\`。向 Cloudflare 注册和传输流量受其[服务条款](https://www.cloudflare.com/terms/)约束。

usque 保持服务器公钥校验开启，不传入 `--insecure`。本机代理只监听 `127.0.0.1`，局域网里的其他设备无法使用。

伪装域名不能保证所有网络都能连接，也不能保证网络设备无法识别 WARP。请遵守适用的网络使用规则。

## 卸载

1. 在程序里关掉「开机时自动连接」，再点「退出」。
2. 删除「WARP 新开关」文件夹，以及 `%LOCALAPPDATA%\WarpSwitchNext`。

## 从源码构建

需要 Windows、.NET SDK（6 或更新）、Go 1.25 或更新、git。

```powershell
git clone https://github.com/hdd15205051972-debug/warp-switch-next-windows.git
cd warp-switch-next-windows
./build.ps1
```

`fetch-deps.ps1` 从 usque 官方 Release 下载 4.2.1（核验固定的 SHA-256），并从源码编译 sing-box 1.14.2（只含程序用到的功能，约 36 MB）。输出为 `dist/WARP 新开关/` 和 zip 安装包。依赖二进制不存放在源码仓库。

程序图标来自 `assets/AppIcon.png`，换图后运行 `python3 tools/make_icon.py` 再重新构建。

## 测试

每次推送都会在 GitHub 的 Windows 电脑上自动编译并测试；`main` 分支出现新版本号且全部通过后自动发布 Release。

- `--self-test`：usque 命令行和 sing-box 配置与参考项目一致、域名纠正、代理规则、设置和账号文件。
- `--proxy-test`：设置并恢复系统代理的各种情况。
- `--connect-test`：真实注册账号，usque + sing-box 连通 WARP，通过系统代理验证 `warp=on`，**连接中更换伪装域名**后仍然连通，最后确认代理已恢复。
- `--diagnose-test`：网络诊断的每一步，以及用 usque 实际连接每个候选域名。
- `tests/ui-test.ps1`：用真实鼠标和键盘操作窗口——在输入框里用中文句号输入域名、点「应用」、连接、连接中改域名、点「网络诊断」、关闭——并截图。

GitHub 的测试电脑在美国，网络不封 WARP，所以「在学校网络上能不能连」只能靠你在学校用「网络诊断」验证。

## 许可

MIT，见 `LICENSE`。usque 以 MIT 许可发布（`USQUE-LICENSE.txt`）；sing-box 以 GPL-3.0-or-later 发布，安装包里附带它的许可证和对应源码地址（`SING-BOX-LICENSE.txt`、`THIRD-PARTY.txt`）。
