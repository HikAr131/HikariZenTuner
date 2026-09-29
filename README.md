# HikariZenTuner

1U「超频调校 · CPU 调校 · 逐核调教」用的命令转发组件：把 JSON Lines 请求转成 [ZenStates-Core](https://github.com/irusanov/ZenStates-Core) 的调用，再把结果写回标准输出。它只做这一件事——不含任何调参逻辑、不写磁盘、不联网，只经 stdin / stdout 与调用方通信。

A small command forwarder used by the 1U desktop app: it turns JSON Lines requests into ZenStates-Core calls (per-core AMD Curve Optimizer read / write / slot probe, Fmax, PBO scalar, temperatures, the raw head of the PM table) and prints the answers. It reports raw facts and leaves their meaning to the caller: no tuning logic, no disk writes, no network.

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
- `serve` 先输出 `{"ok":true,"event":"ready",...}`，之后每读到一行 `{"id":1,"cmd":"read"}` 就回一行带同一个 `id` 的结果；`{"cmd":"quit"}`、标准输入关闭或 `--watch-pid` 指向的进程退出时结束。`probeSlots` 与 `setMap` 只能在 `serve` 里用。
- `identify`：处理器、代号、SMU 版本、PawnIO 状态，以及核心映射（CCD 内的物理槽位，按熔断表跳过关掉的槽位，并与 Windows 看到的物理核与 L3 分组核对；对不上时 `map.trusted = false`）。`map.source` 是 `fuses`（按熔断表推出）或 `override`（本会话用 `setMap` 换过）。
- `read`：每个核心的 Curve Optimizer（回读值按补码还原成有符号数）、Fmax、PBO Scalar，以及 `tctl`（与 `telemetry` 的同一来源、同一筛法）与 `pmTable`。
- `write`：逐核写入、逐核回读，回包里带写前值、SMU 是否接受、回读值。
- `telemetry`：Tctl、各 CCD 温度（SMN 寄存器）与 `pmTable`。每核电压 / 频率不在这里提供。
- `pmTable`：PM 表刷新成功时是 `{"version": 表版本号, "head": [表里前 32 个 float]}`（NaN、无穷大与绝对值超过 1e6 的记 `null`），刷新失败时是 `null`。组件不解释这些位置各是什么——哪一项是功耗、电流还是温度，由调用方按表版本自己判断。
- `probeSlots`：`{"cmd":"probeSlots","slots":[{"ccd":0,"slot":3,"probe":-1,"alternate":-2}],"expectCodename":"GraniteRidge","expectCores":8}`（`alternate` 可省）。逐个槽位：读写前值 → 写入 `probe` → 回读 → 写回写前值 → 回读（对不上再写回一次）；读不出写前值的槽位不写入。写前值恰好等于 `probe` 时改写 `alternate`；没给 `alternate` 就不写这个槽位（`code` 为 `PROBE_EQUALS_BEFORE`，`restored: true`）。回包 `{"results":[...],"halted":false}`，每项带 `probe`（实际写入的值，没写入时为 `null`）、`readable`、`before`、`probeAccepted`、`probeReadback`、`restoreAccepted`、`restoreReadback`、`restored`、`code`（另有 `beforeRaw`、`restoreAttempts`）。写回两次仍对不上时该项 `code` 为 `RESTORE_FAILED`，后面的槽位不再处理（`SKIPPED`，`halted: true`）。哪些槽位是真实核心由调用方判断。
- `setMap`：`{"cmd":"setMap","cores":[{"core":0,"ccd":0,"slot":0}]}`。只换掉本进程内存里的核心映射，不碰处理器、不写磁盘，进程退出即失效。要求 `core` 恰好是 0 到 n−1 且按 (ccd, slot) 升序排列、(ccd, slot) 不重复、CCD 已启用、槽位 0–7，核数与每个 CCD 的核数和 Windows 看到的物理核与 L3 分组逐项一致。通过后回包带新的 `map`（`source` 为 `override`，`trusted` 按与熔断表同一套判据重算）；不通过回 `BAD_REQUEST`，映射不变。

## 写入的护栏 / Write guards

- 只接受整数，且 **只允许 ≤ 0**（不做正偏移）；Zen 3 下限 −30，Zen 4 / Zen 5 下限 −50。
- 只对桌面同核心的代号开放写入（Vermeer、Raphael、DragonRange、GraniteRidge）；APU 只读。
- 核心映射对不上时拒绝写入；PCI 总线锁 5 秒拿不到就放弃（`SMU_BUSY`），不硬等。
- 写入前先发一次 SMU 测试消息，不响应就一个核都不写；回读出现意料之外的值立刻停止后面的核。
- `probeSlots` 守同样的规矩：`probe` 与 `alternate` 只能是 ≤ 0 的整数且不低于该代下限（`alternate` 还必须与 `probe` 不同），只对上面几个桌面代号开放，先发测试消息。它按 (ccd, slot) 直接寻址，所以不要求核心映射可信（它本来就是用来核对映射的），但 CCD 必须已启用、槽位 0–7、最多 64 项，任何一项不合法整个请求都不执行。写回的是读出来的原值（BIOS 里设过正值时就写回那个正值），两次写回仍对不上就停下。
- 组件目录里出现 `inpoutx64.dll` / `WinIo32.dll` 一类旧式端口驱动时拒绝启动。

## 构建 / Build

需要 git 与 .NET SDK 8 以上（构建 net20 / net48 目标时会从 nuget.org 取 Microsoft 的参考程序集包）：

```powershell
git clone --recurse-submodules https://github.com/HikAr131/HikariZenTuner.git
cd HikariZenTuner
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

产物在 `dist\`（`HikariZenTuner.exe`、`ZenStates-Core.dll` 与 `SHA256SUMS.txt`）。构建是确定性的：同一个提交构建出来的文件逐字节相同。1U 分发的 `HikariZenTuner.exe` 另外带了 1U 的代码签名（签名不改变代码本身）。

`--self-test` 只做离线检查（JSON 编解码、写入 / 探测 / 换映射请求的校验、PM 表数值的清洗、核心掩码与负压编码，与 ZenStates-Core 的 `Utils.MakePsmMarginArg` 逐值比对），不碰驱动与处理器。
