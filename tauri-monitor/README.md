# EtherCAT 测试台 · Tauri 桌面版

## Tauri 桌面运行

Windows 上需要 Rust 的 MSVC 工具链、Visual Studio C++ Build Tools 和 WebView2 Runtime。前端使用静态资源，桌面构建无需 npm。安装环境参考 [Tauri 官方文档](https://v2.tauri.app/start/prerequisites/)。

在 Visual Studio 的 x64 Developer PowerShell 中运行：

```powershell
cd tauri-monitor/src-tauri
cargo build --release --locked --target x86_64-pc-windows-msvc --features custom-protocol
```

`target/x86_64-pc-windows-msvc/release/ethercat-monitor.exe` 启动后直接打开真机测试界面。真机测试需要 ABI v3 的 `EtherCatBench.Soem.dll` 同目录，另需 Windows 上已安装的 Npcap。

1. 程序启动时自动刷新一次网卡列表。手动选择专用网卡和主站 PDO 周期，然后点击 **连接 / 扫描**。网卡有变动时可点击 **刷新网卡**。连接相同网口前，先退出其他主站。`1 ms` 默认启用经过 WinForms 台架对照的 500 µs 提前唤醒，连接前可取消。
2. 导入一份或多份 ESI。扫站会按厂商 ID、产品代码和修订号选中匹配设备，也可手动切换。进入 **SAFE-OP** 后读取在线 PDO 分配与映射，实时显示 TxPDO、WKC 和周期曲线。存在其他映射时，可从原始过程数据核对实际字节。
3. PDO 写入需要正常 OP、WKC、身份与在线映射校验通过，并手动解锁。WKC 为 `0`、AL 查询失败或离开正常 OP 时立即锁定并清零。其他 PDO 失配或收发失败连续三次才锁定。短暂异常期间暂停接受新写入，WKC 恢复期望值后可继续写入。触发锁定后仍须手动解锁。已有六轴布局还可在 **灵巧手** 查看热图、逐轴或整组提交，以及读取设备信息与配置。
4. **SDO** 遵循所选 ESI 的权限与状态限制，支持写后回读和手工十六进制访问。顶部 **DC 同步** 可展开配置 Free Run 或 ESI 声明的 SYNC0 `0x0300`，应用时切换到 PRE-OP，入口显示已应用的状态。SDO 与 PDO 共用通信线程，邮箱操作可能拉长周期。
5. **日志** 可采集逐周期计时、AL 状态码和进程 CPU。勾选长跑后自动记录约 10 分钟，使用 **保存 PDO 计时…** 导出 CSV。断开会尝试清零和完成桥接库 INIT 收尾。

首版本机测试采用单从站拓扑和从站在线 PDO 映射。灵巧手六轴控件使用现有地址布局，其他型号可先用通用 PDO 和 SDO 页联调。

若解锁后自动返回锁定，顶部会保留上次自动锁定原因，日志会记录触发周期。查看该事件可区分 PDO WKC、AL 查询超时和离开 OP，即使状态已恢复也能找到当时原因。

## 真机 PDO 对比基线

使用重构分支的 WinForms 计时版和 Tauri 版。退出同一网口上的其他主站，选择相同网卡与从站，在主站周期设置 `1 ms` 后连接。进入同一工作状态后分别采集以下场景，每组建议采 60 秒：

1. 仅监视 PDO。
2. 打开现有的界面快照 CSV，再采 PDO 计时。
3. 锁定输出后执行正常的 SDO 读取，保留该段 PDO 计时。

在 **日志** 点击 **开始 PDO 分段采样**，然后点击 **保存 PDO 计时…**。这份 CSV 每周期一行，含主站调用间隔、SOEM 交换耗时、WKC 和累计跳过时隙。普通采样最多存 120,000 个周期。勾选 **长跑 10 分钟** 可按实际用时持续采集十分钟，缓冲上限为 660,000 个周期。到点或缓冲填满后自动停止，导出由后台写盘。关闭程序前保存。不同轮次记录测试状态、DC 模式和网卡名称。

分段字段使用微秒。`timer_return_offset_us` 是 Windows 计时器等待返回相对截止时间的偏差，负数表示提前返回。`dispatch_late_us` 是 1 ms 调度的实际调用启动偏差。`al_before_exchange_us` 累计自上一次采样以来的 AL 查询耗时。`send_us` 是 SOEM 发送调用耗时，包含送入 Npcap 的开销。`receive_us` 是 SOEM 接收调用耗时，包含等待回帧和处理。未执行或不可用的阶段留空。使用配套 DLL 可得到收发分段，旧 ABI v3 DLL 可以继续使用，收发分段留空。

用以下命令汇总间隔的 P50、P99、P99.9、最大值及跳过时隙。新 CSV 还会汇总各阶段的样本数、P50、P99 和最大值，旧 CSV 仍可分析。

```powershell
.\tauri-monitor\Measure-PdoTrace.ps1 -Path .\ethercat-timing.csv
```

### 1 ms 调度 A/B

将主站 PDO 周期设为 `1 ms`。先取消勾选 **提前唤醒 + 短自旋**，连接并进入 OP，保持输出锁定，采样约 60 秒。保存后断开，勾选该选项，同条件再采一组。勾选时把高分辨率计时器设置为在 PDO 截止时间前 500 µs 返回，再短自旋等到原截止时刻。已进入最后 500 µs 时直接短自旋。CSV 列 `scheduler_spin_lead_us` 标识所选模式，`0` 为原模式，`500` 为提前唤醒模式。`timer_return_offset_us` 仍相对原 PDO 截止时间，直接自旋时留空。

长跑对比时两组都勾选 **长跑 10 分钟**。CSV 首行的 `capture_wall_seconds` 是采样起止的实际用时，`process_cpu_percent` 是同一时段内上位机进程的平均 CPU 占用，按逻辑处理器数归一化。CPU 取样覆盖程序在采集期间的工作，包含界面，导出发生在计时窗口之后。用两组 CSV 比较实际频率、P99.9、WKC 失配、跳过时隙、发送尖峰和进程 CPU。

Tauri 的 `process_cpu_percent` 对应 Rust 主进程。对比整个桌面程序的 CPU 时，还需计入它的 WebView2 子进程。

`al_status_code` 是最近一次成功查询得到的 AL Status Code，`al_status_age_us` 是从该次查询到本行 PDO 启动的时间。AL 查询超时后保持这两列为空，直到收到新的状态回复。
