# HikariZenTuner

1U「超频调校 · CPU 调校 · 逐核调教」用的命令转发组件：把 JSON Lines 请求转成 [ZenStates-Core](https://github.com/irusanov/ZenStates-Core) 的调用，再把结果写回标准输出。它只做这一件事——不含任何调参逻辑、不写磁盘、不联网，只经 stdin / stdout 与调用方通信。

A small command forwarder used by the 1U desktop app: it turns JSON Lines requests into ZenStates-Core calls (per-core AMD Curve Optimizer read / write, Fmax, PBO scalar, temperatures) and prints the answers. No tuning logic, no disk writes, no network.

## 许可证 / License

- 本仓库的代码：**GPL-3.0-or-later**（`LICENSE` 是 GPL v3 全文）。
- ZenStates-Core（irusanov）：GPL-3.0，以 git 子模块钉在上游提交 `8979d2790467f31cdf032fbf19f89fbf9693ff08`（v1.91），原样从源码构建，没有任何改动。
- PawnIO 驱动（namazso）：不在本仓库里。ZenStates-Core 经它的已签名驱动访问处理器；驱动的安装程序由 1U 单独分发，官网 <https://pawnio.eu/>。

1U 与本组件是两个独立的程序：1U 只经标准输入 / 输出与它交换 JSON 文本，不链接、不引用本组件或 ZenStates-Core 的任何代码。

## 命令 / Protocol

```text
HikariZenTuner.exe identify | read | telemetry | snapshot
HikariZenTuner.exe write --co "0:-20,3:-26" [--continue-on-failure]
HikariZenTuner.exe serve --watch-pid <pid>
HikariZenTuner.exe --self-test | --version
```

- 一次性命令输出一行 JSON 后退出（`ok` 为真时退出码 0）。
- `serve` 先输出 `{"ok":true,"event":"ready",...}`，之后每读到一行 `{"id":1,"cmd":"read"}` 就回一行带同一个 `id` 的结果；`{"cmd":"quit"}`、标准输入关闭或 `--watch-pid` 指向的进程退出时结束。
- `identify`：处理器、代号、SMU 版本、PawnIO 状态，以及核心映射（CCD 内的物理槽位，按熔断表跳过关掉的槽位，并与 Windows 看到的物理核与 L3 分组核对；对不上时 `map.trusted = false`）。
- `read`：每个核心的 Curve Optimizer（回读值按补码还原成有符号数）、Fmax、PBO Scalar。
- `write`：逐核写入、逐核回读，回包里带写前值、SMU 是否接受、回读值。
- `telemetry`：Tctl 与各 CCD 温度（SMN 寄存器）。每核电压 / 频率不在这里提供。

## 写入的护栏 / Write guards

- 只接受整数，且 **只允许 ≤ 0**（不做正偏移）；Zen 3 下限 −30，Zen 4 / Zen 5 下限 −50。
- 只对桌面同核心的代号开放写入（Vermeer、Raphael、DragonRange、GraniteRidge）；APU 只读。
- 核心映射对不上时拒绝写入；PCI 总线锁 5 秒拿不到就放弃（`SMU_BUSY`），不硬等。
- 写入前先发一次 SMU 测试消息，不响应就一个核都不写；回读出现意料之外的值立刻停止后面的核。
- 组件目录里出现 `inpoutx64.dll` / `WinIo32.dll` 一类旧式端口驱动时拒绝启动。

## 构建 / Build

需要 git 与 .NET SDK 8 以上（构建 net20 / net48 目标时会从 nuget.org 取 Microsoft 的参考程序集包）：

```powershell
git clone --recurse-submodules https://github.com/HikAr131/HikariZenTuner.git
cd HikariZenTuner
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

产物在 `dist\`（`HikariZenTuner.exe`、`ZenStates-Core.dll` 与 `SHA256SUMS.txt`）。构建是确定性的：同一个提交构建出来的文件逐字节相同。1U 分发的 `HikariZenTuner.exe` 另外带了 1U 的代码签名（签名不改变代码本身）。

`--self-test` 只做离线检查（JSON 编解码、写入请求的校验、核心掩码与负压编码，与 ZenStates-Core 的 `Utils.MakePsmMarginArg` 逐值比对），不碰驱动与处理器。
