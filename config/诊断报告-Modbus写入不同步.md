# Modbus 写入不同步问题诊断报告（修订版）

> 现场：192.168.5.30 / Port 502 / S7-1200/1500 MB_SERVER  
> 现象：软件显示"正常连接"，写入返回成功，但 PLC 端数据未变化  
> 修订说明：初版误判为"Coil 地址偏移算错"，把地址改成 00001-00012 后实测仍无效。  
> 本版根据 DB4 实测结构 + MB_SERVER 标准行为重新定位根因。

---

## 一、根因（结论先行）

**C# 用线圈功能码（FC1/FC5/FC15）写命令，但西门子标准 MB_SERVER 的线圈不通过 `MB_HOLD_REG` 访问——它写入 MB_SERVER 自己的背景 DB（DB3）内部线圈缓冲区。而你的 PLC 程序读的是 `DB4.coils[0]/coils[1]`。两块内存互不相干，所以"写入返回成功"（协议帧确实发出并得到了 MB_SERVER 的正确应答），但 DB4 数据永远不变。**

HR（40001 起，FC3/FC6/FC16）是正确的，因为 `MB_HOLD_REG` 指向 DB4，保持寄存器确实落在 DB4 上。问题**只在线圈**。

地址用 00257 还是 00001 都不影响——根因不是偏移算错，而是**整个线圈地址空间指向了另一块 DB**。

---

## 二、证据链

### 2.1 DB4 实测结构（用户提供）

| 元素 | 类型 | 字节偏移 | 说明 |
|------|------|---------|------|
| HR_SetLevel | Real | 0.0 | 设定液位（上位机 → PLC） |
| HR_SetSpeed | Real | 4.0 | 设定速度（上位机 → PLC） |
| Static_1 | Byte | 8.0 | 保留 |
| IR_Level | Real | 10.0 | 实际液位（PLC → 上位机） |
| IR_Speed | Real | 14.0 | 实际速度（PLC → 上位机） |
| IR_Count | DInt | 18.0 | 总产量（PLC → 上位机） |
| Static_2 | Byte | 22.0 | 保留 |
| **coils[0]** | **Byte** | **24.0** | **8 个命令位（Line.Start/Stop/Auto/EStop）** |
| **coils[1]** | **Byte** | **25.0** | **8 个状态位（Running/Alarm/Valve/Conveyor）** |

### 2.2 PLC 程序意图

```scl
// 命令：从 coils[0] (byte 24) 读
"DBMO"."Line.Start"    := "DB_Modbus_Server".coils[0].%X0;
"DBMO"."Line.Stop"     := "DB_Modbus_Server".coils[0].%X1;
"DBMO"."Line.AutoMode" := "DB_Modbus_Server".coils[0].%X2;
"DBMO"."Line.EStop"    := "DB_Modbus_Server".coils[0].%X3;

// 状态：写到 coils[1] (byte 25)
"DB_Modbus_Server".coils[1].%X0 := "DBMO"."Line.Running";
"DB_Modbus_Server".coils[1].%X1 := "DBMO"."Line.AlarmActive";
"DB_Modbus_Server".coils[1].%X2 := "DBMO"."Filling.ValveOpen";
"DB_Modbus_Server".coils[1].%X3 := "DBMO"."Conveyor.Running";
```

### 2.3 为什么线圈写不进 DB4

西门子 S7-1200/1500 标准 MB_SERVER 指令的地址空间分流：

- `MB_HOLD_REG` 参数：**仅映射保持寄存器**（FC3/FC6/FC16）到指定 DB（这里是 DB4）。
- **线圈（FC1/FC5/FC15）与离散输入（FC2）不通过 `MB_HOLD_REG`**，而是存放在 MB_SERVER 的**背景数据块（实例 DB3）内部缓冲区**。
- 标准 MB_SERVER 块上**没有 `MB_COILS_START` 之类参数**可把它改指到 DB4。

所以数据通路是：

```
C# 写 coil 00001 ──FC5──▶ MB_SERVER ──▶ DB3 内部线圈缓冲   ✗ DB4 不动
PLC 程序读 DB4.coils[0]  ──────────────────────────────▶ 永远 0
C# 读 coil 00009 ──FC1──▶ MB_SERVER ──▶ DB3 内部线圈缓冲   ✗ 取不到 DB4 的状态
```

---

## 三、修复方案（三选一）

### 方案 A（推荐，风险最小）：用 HR 寄存器读写 coils 字节

coils[0]/coils[1] 本来就躺在 DB4 的 byte 24-25，而 DB4 已被 `MB_HOLD_REG` 覆盖，直接用 HR 寄存器访问这两个字节即可，绕开线圈地址空间。

Modbus 保持寄存器映射：`HR 地址 N（40001 基）= DB4 字节偏移 (N-40001)*2`
- **40013 = 字节 24-25 = coils[0](byte24) + coils[1](byte25)**

C# 侧要点：
- 命令：FC6 写 **40013**，把 8 个命令位打包进 **byte 24**（大端：高字节=byte24）。  
  例：置 Line.Start(bit0)=1 → `value = 0x01 << 8 = 0x0100 = 256`
- 状态：FC3 读 **40013**，取 **byte 25**（低字节）解析 4 个状态位。

⚠️ 陷阱：40013 同时覆盖 coils[0] 和 coils[1]，若 C# 写命令时把 coils[1]（PLC 写的状态）也覆盖掉，状态会丢失。处理办法：
- (a) 写前先读 40013，保留低字节再写回（读-改-写）；或
- (b) 采用方案 C，把命令/状态拆到两个独立寄存器。

### 方案 B（不改 C#，改 PLC）：把 DB3 线圈缓冲搬到 DB4

在 PLC 循环 OB 里加拷贝（DB3 为 MB_SERVER 背景 DB）：

```scl
"DB_Modbus_Server".coils[0] := "MB_SERVER_DB".<线圈缓冲字节0>;
"DB_Modbus_Server".coils[1] := "MB_SERVER_DB".<线圈缓冲字节1>;
```

缺点：DB3 内部线圈缓冲的**精确偏移随 MB_SERVER 固件版本变化**（V4.x 与旧版不同），移植/升级易失效，且需打开 DB3 实例 DB 确认偏移。不建议作长期方案。

### 方案 C（最干净，推荐正式项目）：新增专用控制/状态字，彻底不用线圈

在 DB4 末尾扩展两个 UInt16：
- `HR_Control` 字节 26（寄存器 40014）—— C#→PLC 命令
- `HR_Status`  字节 28（寄存器 40015）—— PLC→C# 状态

PLC 侧映射改为：

```scl
"DBMO"."Line.Start"    := "DB_Modbus_Server".HR_Control.%X0;
"DBMO"."Line.Stop"     := "DB_Modbus_Server".HR_Control.%X1;
"DBMO"."Line.AutoMode" := "DB_Modbus_Server".HR_Control.%X2;
"DBMO"."Line.EStop"    := "DB_Modbus_Server".HR_Control.%X3;
"DB_Modbus_Server".HR_Status.%X0 := "DBMO"."Line.Running";
"DB_Modbus_Server".HR_Status.%X1 := "DBMO"."Line.AlarmActive";
"DB_Modbus_Server".HR_Status.%X2 := "DBMO"."Filling.ValveOpen";
"DB_Modbus_Server".HR_Status.%X3 := "DBMO"."Conveyor.Running";
```

C# 侧 8 个 coil 标签全部改成 HR 标签（`dataType=UInt16`，address `40014` / `40015`）。  
优点：HR 链路已验证可用，命令/状态分离无冲突，调试直观，符合"数字量打包进寄存器"的工业惯例。

---

## 四、HR 部分保持原样（已验证正确）

| Tag | 地址 | PLC 字节偏移 | 解释 |
|-----|------|------------|------|
| mb-hr-setlevel | 40001 | 0-3 (Real) | HR_SetLevel ✓ |
| mb-hr-setspeed | 40003 | 4-7 (Real) | HR_SetSpeed ✓ |
| mb-ir-level | 40006 | 10-13 (Real) | IR_Level ✓ |
| mb-ir-speed | 40008 | 14-17 (Real) | IR_Speed ✓ |
| mb-ir-count | 40010 | 18-21 (DInt) | IR_Count ✓ |

---

## 五、验证步骤（以方案 C 为例）

> 注：HR_Control/HR_Status 的实际字节位置由用户在 TIA 中决定（直接覆盖原 coils 字节 24-25）。下面是按"HR_Control 在字节 24 / 寄存器 40013、HR_Status 在字节 26 / 寄存器 40014"的示例，**以你 DB4 实际字节布局为准**。

1. 在 TIA 里给 `DB_Modbus_Server` 增加 `HR_Control`、`HR_Status` 两个 UInt16（常见做法是直接放在原 coils 占用的字节位置 24、26 上），下载到 PLC。
2. 把 PLC 程序里的 `coils[0]/coils[1]` 改成读/写 `HR_Control`/`HR_Status` 的 `%X0..%X3`。
3. 上位机根据实际字节位置调整 `production-line.json` 中命令/状态标签的 `modbusAddress`（例如 `HR_Control` 在字节 24 → 寄存器 40013，命令标签都用 `40013 + bitIndex`），重启加载。
4. 写 `Line.Start=true` → 监视 `DB_Modbus_Server.HR_Control.%X0` 应看到对应位变 1 → `DBMO.Line.Start` 变 true。
5. 强制 `DBMO.Line.Running=true` → 上位机读对应状态字应能解析出 Running 位为 1。

---

## 六、面试延伸：Modbus 写入成功但数据不变，怎么排查？

1. **抓包确认**：Wireshark 看请求/响应帧，确认 Function Code、Slave ID、Address、Value。
2. **区分地址空间**：Modbus 的 Coil(0xxxx)、DI(1xxxx)、HR(4xxxx)、IR(3xxxx) 是四条独立空间，和 PLC DB 的映射关系要逐一核对。
3. **S7 MB_SERVER 经典坑**：`MB_HOLD_REG` 只管 HR/IR，线圈/DI 走实例 DB 内部缓冲——这正是本项目踩的坑。
4. **协议层成功 ≠ 应用层正确**：MB_SERVER 对"写到实例 DB 缓冲区"必然回 SUCCESS，但 PLC 程序若读的是另一块 DB，数据自然不变。
5. **字节序/字长**：Float32 占 2 个寄存器、大端 vs 小端、DInt 的字节序都要对齐。
6. **权限**：PLC 侧某些点是否被设为只读或被更高优先级逻辑覆盖。

**核心原则**：必须把"协议地址 → PLC 字节偏移 → 程序变量"整条链路一一对齐，任何一环错位都会出现"看似成功但无效"。

---

## 七、补充：写入"假成功"问题审查与修复（2026-08-10）

### 7.1 现象
无论连接正常还是失败，UI 都提示"写入成功"。

### 7.2 审查结论（调用链全追踪）

驱动 `ModbusTcpDriver.WriteTagAsync` 与两个 ViewModel（`ProductionMonitorViewModel` / `DeviceDetailViewModel`）的 try/catch 逻辑本身是对的：驱动抛异常 → UI 显示"写入错误"。因此"假成功"不来自异常吞没，而来自**协议层无法感知的失败**与**环境回退**：

| # | 根因 | 说明 |
|---|------|------|
| 1 | **ConnectAsync 只做 TCP 验证** | TCP 连上即 `_connected=true`，从不确认 MB_SERVER 真实响应。"连接成功"可能是假连接。 |
| 2 | **写入只看"没抛异常"** | NModbus 只要收到从机正常响应帧就不抛异常。写入落在错误的地址空间（DB3 vs DB4）时，从机照样 ACK → "写入成功"，数据却没变。 |
| 3 | **从机异常帧信息不直观** | MB_SERVER 拒绝越界地址（如 DB4 尚未加入 HR_Control/HR_Status 时写 40014）会返回异常码 0x02，原代码抛出的是库的笼统异常。 |
| 4 | **Mock 设备静默回退** | `App.xaml.cs`：配置文件加载失败/无设备成功启动时，自动创建 Mock 设备。Mock 驱动写入永不失败 → 永远"写入成功"。 |
| 5 | **同名标签跨设备** | `production-line.json` 第一台 OPC UA 设备与 Modbus 设备标签重名（Line.Start 等），UI 若写的是 OPC UA 设备，实际走 `OpcUaDriver`。 |
| 6 | **旧 DLL 未重建** | 热重载只重载配置，驱动 DLL 需重新编译+重启应用才生效。 |

### 7.3 已实施的修复（`Plugins/Drivers.Modbus/ModbusTcpDriver.cs`，编译通过）

1. **ConnectAsync 增加 Modbus 握手探测（非致命）**：连接后读 1 个保持寄存器（寄存器 0）作为协议栈活性检查。探测失败仅忽略，不阻断连接——避免慢速/抖动网络下误判为假连接。真正的"假成功"由写后回读校验捕获。对 S7 MB_SERVER 而言端口 502 由 MB_SERVER 打开，TCP 连通本身已证明 MB_SERVER 在监听，握手仅作辅助诊断。
2. **WriteTagAsync 增加写后回读校验**：
   - 线圈写入 → 读回 1 位比对；
   - HR 按位写入（Bool+BitIndex）→ 读回寄存器比对目标位；
   - HR 整寄存器写入 → 读回全部寄存器逐个比对。
   - 不一致即抛 `InvalidOperationException("写入回读校验失败: ...")` → UI 显示"写入错误"。协议 ACK ≠ 写入成功。
3. **SlaveException 明确化**：从机拒绝（异常码 0x02 地址越界等）包装为 `"Modbus 从机拒绝写入请求 (异常码 2): ..."`，UI 可直接看到拒绝原因。
4. **ReadTagsAsync 批量读失败自动降级**：合并读取（批量读）被从机拒绝（如某个地址越界返回 SlaveException）时，不再把整组标 Bad，而是降级为逐标签单点读取，隔离坏点、其余正常。仅传输层异常（IO/超时）才整组失败并触发重连。

### 7.5 补充：全标签 Bad 的根因 —— 合并读取"一损俱损"（2026-08-10 晚间）

**现象**：方案 C 配置（40001~40010 原 HR + 40014/40015 新控制/状态字）加载后，**全部 13 个标签 Quality=Bad**，连本应正常的 40001/40003/40006/40008/40010 也 Bad。

**根因**：`MergeAdjacentAddresses` 把相邻地址合并为一次大范围读（40001~40015 → 一次性读寄存器 0~14 共 15 个）。而 **DB4 目前只有字节 0~25（寄存器 0~12），40014/40015 对应的字节 26~29 在 DB4 中不存在**，MB_SERVER 对越界地址返回异常码 0x02 → 整个批量读请求失败 → 整组标签全部被标 Bad。

**教训**：Modbus 批量读是"一损俱损"——一个地址越界，同组的正常地址也读不到。工业现场对批量读要：
1. 确保合并范围内所有地址在从机端真实存在（配置与 PLC 数据块严格对齐）；
2. 或驱动实现"批量失败 → 单点降级"（本驱动已实现，见 7.3-4）。

**当前状态**：PLC 侧 DB4 补齐 `HR_Control`（字节 26）/ `HR_Status`（字节 28）并下载后，批量读自然全部成功；在此之前，降级逻辑会让 5 个 HR 浮点恢复读数，仅 8 个 BitIndex 标签保持 Bad（属预期信号）。

### 7.4 需要你做的核对

1. **重建 + 重启应用**（配置热重载不会更新 DLL）。
2. **确认写入的设备**：UI 上写的是 Modbus-PLC（192.168.5.30）而非 OPC UA 设备或 Mock 设备。
3. **当前 DB4 还没有 HR_Control/HR_Status**：此时写 40014 会看到"Modbus 从机拒绝写入请求 (异常码 2: 非法数据地址)"——这是**正确信号**，说明驱动校验生效。把两个 UInt16 加进 DB4 后即可正常写入。
4. 若写入"成功"但 PLC 数据仍不变：用 Wireshark 抓包确认 Function Code / Address / Value 与 PLC 端映射一致。
