# 计算规则与报警规则运行逻辑

## 1. 规则存在哪里

系统以当前选中的业务数据库作为唯一持久化来源：

- 计算规则：`calculation_rules` 表。
- 报警规则：`alarm_definitions` 表。
- 计算结果对应的虚拟资源：`resource_nodes` 表中的 `Calculated/*` 节点。
- 报警触发、确认、恢复历史：`alarm_histories` 表。
- 新增、修改、删除规则的操作审计：`security_audits` 表。

`config/calculation-rules.json` 和 `config/alarm-definitions.json` 仅用于批量导入及热更新。文件内容会写入数据库，运行时仍从数据库重建快照，文件不是第二套并行运行规则。

设备数据点可以在 `production-line.json` 中配置 `templateId`。客户端首次发现模板关联但数据库不存在对应报警规则时，会由报警模板生成规则并写入 `alarm_definitions`。生成后规则会显示在报警规则页面；工程师在页面保存后，规则来源改为用户配置，启动过程不会覆盖人工修改。

## 2. 为什么以前页面为空但仍会报警

旧实现的 `App.RegisterDynamicAlarmRules` 在启动时按 TagName 硬编码液位、速度和急停规则，并直接注册到旧的 `AlarmManager` 内存列表。这些规则没有写入 `alarm_definitions`，因此配置页面查不到，但内存规则仍会触发。

当前实现已经删除这条隐藏链路。报警页面、数据库仓储、规则引擎和趋势报警线全部读取同一份 `AlarmDefinition` 快照。

## 3. 计算规则的用途

计算规则用于把一个或两个现场数值加工成新的实时数据点，例如：

- 两个传感器取平均值。
- 实际值减去设定值，得到偏差。
- 流量乘以换算系数，得到质量流量。
- 已完成数量除以计划数量，得到完成率。
- 两个液位相减，得到液位差。

计算结果不是一次性报表值，而是持续更新的虚拟数据点。它与普通数据点一样可以被报警、趋势、权限和后续计算引用。

## 4. 傻瓜式新增计算规则

进入“系统设置 → 计算规则”：

1. 点击“新增计算”。
2. 选择输入数据点 A。
3. 选择计算方式；需要两个输入时再选择 B，需要系数时填写系数。
4. 填写输出名称并确认自动生成的公式。
5. 点击“保存并立即生效”。

系统自动完成以下内容：

- 生成稳定的 `RuleId`。
- 以 TagId 保存输入绑定，避免不同设备的同名 Tag 串值。
- 使用 `A`、`B` 生成安全表达式，用户无需编写脚本。
- 生成 `virtual-{RuleId}` 输出 TagId。
- 创建 `Calculated/{RuleId}` 资源路径。
- 持久化到 `calculation_rules`。
- 热替换 `DataProcessor` 运行时快照。
- 写入规则变更审计日志。

## 5. 计算规则运行流程

```text
设备驱动采集 TagValue
        ↓
HistoryWriter 写入 RealTimeStore
        ↓
DataProcessor 按输入 TagId 查找计算规则
        ↓
等待 A/B 输入值均已就绪
        ↓
将 A/B 替换为实时数值并计算表达式
        ↓
生成 virtual-{RuleId} TagValue
        ↓
写回 RealTimeStore 并广播
        ↓
趋势 / 报警 / 后续计算规则收到虚拟数据点
```

结构化输入保存示例：

```json
[
  { "alias": "A", "tagId": "mb-ir-level", "tagName": "Filling.ActualLevel", "resourcePath": "Devices/Modbus-PLC/Filling.ActualLevel" },
  { "alias": "B", "tagId": "tag-filling-actuallevel", "tagName": "Filling.ActualLevel", "resourcePath": "Devices/灌装产线/Filling.ActualLevel" }
]
```

即使两个输入的 TagName 相同，运行时也只按 TagId 取值。

## 6. 傻瓜式新增报警规则

进入“系统设置 → 报警规则”：

1. 点击“新增报警”。
2. 选择设备或“计算数据点”分组，再选择具体数据点。
3. 选择“大于、小于、等于、区间”等中文条件并填写阈值。
4. 确认自动生成的报警标题、级别和提示内容。
5. 点击“保存并立即生效”。

一般无需填写 RuleId、AlarmCode、ResourcePath 或表达式。系统会根据所选资源自动生成。延迟、迟滞、冷却和自定义表达式收纳在“高级设置”中。

## 7. 报警规则运行流程

```text
普通或计算 TagValue 写入 RealTimeStore
        ↓
RuleEngineService 按 TagId 找到 AlarmDefinition
        ↓
AlarmRuleBuilder 评估触发、恢复和抑制条件
        ↓
AlarmRuleSignalBus 发布评估信号
        ↓
AlarmStateMachine 执行 Pending / Active / Acknowledged / Cleared
        ↓
AlarmCenter 广播 UI 事件并持久化 alarm_histories
        ↓
趋势图标红、报警日志、弹窗和外部通知适配器消费事件
```

## 8. 已验证的完整流程

自动化集成测试 `CalculationAlarmFlowTests` 使用以下场景：

1. 输入 A 为 80，输入 B 为 160。
2. 计算规则 `(A + B) / 2` 输出平均值 120。
3. 输出写入 `virtual-calc-average`。
4. 报警规则判断虚拟值是否大于 100。
5. 规则引擎发布值为 120 的触发信号。

该测试覆盖“现场输入 → 计算 → 虚拟资源 → 报警评估”，用于防止后续改动只更新页面而没有真正打通运行时。
