# EtherCAT 从站测试台

## 下载

在 [GitHub Releases](https://github.com/1164804963/EtherCAT-/releases) 下载 Windows x64 界面包。解压后运行 `EtherCatBench-Live.exe --demo` 可使用模拟模式。真机联调还需要按照下方构建步骤生成 `EtherCatBench.Soem.dll`，并将它放在 EXE 同目录。Npcap 驱动需在 Windows 上单独安装。

## 真机模式

双击 `EtherCatBench-Live.exe`。程序和 `EtherCatBench.Soem.dll` 放在同一个目录，测试电脑需有 Windows x64、.NET Framework 4.5+ 和 Npcap。程序打开后只枚举网卡，由用户选择专用有线接口并点击 **连接 / 扫描**。当前版本用于一台从站直连，不自动选卡或自动建立连接。 [Npcap](https://npcap.com/#download)

1. **连接。** 将测试网口直连从站的 EtherCAT IN。选择下拉列表中的网卡、设置主站 PDO 目标周期，再点 **连接 / 扫描**。同一网口应由本工具独占使用。页面顶部显示从站身份、实际状态、AL 状态码、WKC 和实测 PDO 间隔。
2. **导入 ESI。** 导入对应厂商、产品码和 Revision 的产品 XML。有多个设备时选实际设备。SDO 表来自 ESI 的对象字典，PDO 字段优先采用从站的在线 CoE 分配与映射。ESI 负责描述字段。导入不会下发 InitCmd、写 EEPROM 或修改映射。
3. **SDO。** PRE-OP 下可读取 `0x1018:01`，或从 ESI 表选中对象读写。可读写对象写后回读。无 ESI 对象字典时用 **手工 Index/Sub**，写入数据用低字节在前的十六进制，例如 `2A 00`。异常显示 SDO Abort Code。OP 下先锁定输出再访问邮箱。
4. **DC。** 连接后选择匹配 ESI 声明的 SYNC0 模式（`AssignActivate 0x0300`），输入 Hz，再点 **切到 PRE-OP 并应用**。例如 1000 Hz 对应 1,000,000 ns。程序会确认从站进入 PRE-OP，再配置 ESC 并回读激活位和周期寄存器，完成后保持 PRE-OP。也可关闭 SYNC0。SYNC1、其他激活组合只显示，暂不可应用。某些固件另需 CoE 同步参数，可在 PRE-OP 通过 SDO 按设备文档配置。 [SOEM DC 实现](https://github.com/OpenEtherCATsociety/SOEM/blob/v2.0.1/src/ec_dc.c)
5. **PDO。** 选择 **SAFE-OP** 后主站按从站默认配置映射并开始交换，之后可以请求 **OP**。在 **PDO 实时监视**看原始字节和字段值。OP 且 WKC 正常，ESI 身份及在线映射匹配时，可显式解锁、选择 RxPDO 字段、填值并应用。锁定会发送零值，TxPDO 只读。CoE 映射无法读取时仍可查看原始字节，字段发送保持锁定。
6. **恢复。** PDO WKC 异常会锁定输出。检测到从站无响应后，点击 **断开**，检查链路，再手动连接和切换状态。**日志**页可以保存采样 CSV。若从站上电有可能驱动负载，首次联调先确保台架不会运动。

主站周期为 Windows 尽力调度，和从站 SYNC0 周期分别设置。1 ms 模式可以在 PDO 页面查看实测最近、平均、最大间隔及跳过的时隙。实际最小稳定周期取决于 Windows、网卡和从站。

## 灵巧手

导入匹配的灵巧手 ESI 后打开 **灵巧手**。六轴表分别显示待提交的角度、力矩和速度、最近有效 PDO 中的下发值，以及 TxPDO 角度和力矩。输出须在 OP、WKC 正常、PDO 映射匹配的条件下手动解锁。**应用选中轴**只提交该轴的三项，**应用全部**同时提交六轴。物理单位和机械行程由设备定义，界面显示原始整数。

**温度与触觉**显示 64 个温度像素和 5 组各 48 个触觉字节，按对象子索引排列。**设备信息与配置**按所选 ESI 列出 `0x9000` 和 `0x8000` 的参数，遵守各子项声明的权限、状态限制、类型和位宽。写入后在当前状态允许读取时自动回读。**读取全部**依次读取表内当前可读项，逐项显示结果并统计成功、失败和跳过。回读表示对象字典值，实际动作、保存、回零和复位由从站固件处理。

## 构建

在 Windows x64 的 PowerShell 中运行：

```powershell
./Setup-Dependencies.ps1
./Build.ps1 -OutputDirectory dist
```

依赖脚本下载并校验 Zig 0.15.2、SOEM v2.0.1 x64 SDK、Npcap SDK 1.17 和构建所需的 SOEM 源码，保存在 `native/deps`。构建使用 .NET Framework 自带的 C# 编译器，生成 `dist/EtherCatBench-Live.exe`、桥接 DLL 和模拟 ESI。Npcap 运行时驱动需在真机测试电脑上单独安装。

仅重编托管界面可运行 `./Build.ps1 -ManagedOnly -OutputDirectory dist`。桥接源码在 `native/bench_soem.c`。复用或对外分发时，请核对相关依赖的许可。 [SOEM v2.0.1 发布](https://github.com/OpenEtherCATsociety/SOEM/releases/tag/v2.0.1) · [SOEM 许可](https://github.com/OpenEtherCATsociety/SOEM/blob/v2.0.1/LICENSE.md) · [Npcap 许可](https://npcap.com/#download)

## 模拟模式

在未连接时点 **模拟模式**，或运行 `EtherCatBench-Live.exe --demo`。将随附的 `DemoESI.xml` 与程序放在同一目录，可演示扫站、状态切换、写 SDO、PDO 序号回显、DC 配置和掉线。运行 `EtherCatBench-Live.exe --self-test` 可执行离线自测，报告写入 `selftest-report.txt`。
