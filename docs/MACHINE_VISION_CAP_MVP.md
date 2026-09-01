# 瓶盖有无检测 MVP

## 1. 功能目标

本模块使用目录图片模拟工业相机，完成瓶盖有无检测的首个可运行闭环。操作人员可以查看实时结果，工程师或管理员可以通过任务向导完成样图目录选择、ROI 设置、模板教学、阈值调整和任务发布。

当前 MVP 使用 OpenCV 模板匹配，适用于相机位置、光源、瓶型和瓶盖姿态较稳定的验证环境。正式产线可在不改变上层资源、趋势和报警链路的情况下替换为真实相机驱动或深度学习算法。

海康驱动运行时动态加载本机 `MvCameraControl.Net.dll`，不会将厂商 SDK 路径固化为项目编译依赖。自定义安装位置可通过环境变量 `HIKVISION_MVS_SDK_PATH` 指定。

## 2. 操作流程

1. 进入“机器视觉”。访客会看到“登录后新增瓶盖检测”，登录工程师或管理员账号后显示新增入口。
2. 点击“新增瓶盖检测”，选择普通目录模拟、海康目录模拟或海康 MVS 相机。
3. 使用海康 MVS 时点击“自动扫描海康相机”，系统会扫描 GigE、USB 和 MVS 虚拟设备，并优先选择虚拟相机。
4. 目录模拟时确保第一张图片是合格样图；MVS 相机会直接抓取当前帧作为教学样图。
5. 使用 0 到 1 的归一化坐标设置 ROI，使检测区域覆盖瓶盖。
6. 设置匹配阈值，点击“教学并测试当前合格样图”。
7. 教学成功后点击“发布任务并注册报警”。
8. 返回工作台点击“开始检测”，或使用“手动触发一次”进行单帧验证。
9. 检测结果会同步显示在实时图像、统计卡片和当前运行历史中；缺盖图片按任务和日期自动保存。

访客账号只有查看权限，启动、暂停、触发、重置和配置按钮会隐藏。

## 3. 运行链路

```text
DirectoryCameraDriver（目录模拟相机）
    ↓ VisionFrame
CapPresenceTemplateAlgorithm（ROI + OpenCV 模板匹配）
    ↓ VisionInspectionResult
VisionInspectionEngine（结果持久化、NG 图片保存）
    ↓ VisionResultPublisher
AcquisitionChannel
    ↓
HistoryWriter → RealTimeStore → RuleEngineService → AlarmStateMachine → AlarmCenter
```

视觉模块不直接弹报警，也不使用 `if/else` 写死报警条件。任务发布时生成动态 `AlarmDefinition`，规则引擎通过统一实时数据事件判断 `Result.CapPresent == false`，恢复条件为 `Result.CapPresent == true`。

## 4. ResourcePath 与数据点

任务发布后会生成以下资源结构：

```text
Vision/{相机名称}/{任务名称}/Status
Vision/{相机名称}/{任务名称}/Result
Vision/{相机名称}/{任务名称}/Statistics
```

主要数据点：

| 分组 | 数据点 | 类型 | 用途 |
| --- | --- | --- | --- |
| Status | `Camera.Connected` | Bool | 模拟/真实相机连接状态 |
| Status | `Task.Running` | Bool | 任务运行状态 |
| Result | `Result.Pass` | Bool | 本次检测是否合格 |
| Result | `Result.CapPresent` | Bool | 是否检测到瓶盖，报警规则目标 |
| Result | `Result.MatchScore` | Float64 | 模板匹配分数，可用于趋势 |
| Result | `Result.ProcessingTime` | Float64 | 单帧处理耗时，可用于趋势 |
| Statistics | `Statistics.TotalCount` | Int64 | 总检测数 |
| Statistics | `Statistics.NgCount` | Int64 | NG 数量 |
| Statistics | `Statistics.PassRate` | Float64 | 良率百分比 |

所有 TagId 由任务 ID 稳定生成；所有 ResourcePath 由相机和任务配置动态生成，不写死现场资源。

## 5. 数据持久化

- `vision_cameras`：相机源及触发参数。
- `vision_inspection_tasks`：算法、模板、ROI、阈值和 NG 存图配置。
- `vision_inspection_records`：每次检测结果、匹配度、耗时和 NG 图片路径。
- `historical_records`：视觉运行时数据点的统一历史记录，可被趋势模块查询。
- 报警定义：沿用系统现有报警定义仓储，发布任务后立即热重载。

SQLite 和 PostgreSQL 均使用同一仓储接口，启动时自动确保视觉表存在。

## 6. 验证范围

自动化测试覆盖：

- 合格瓶盖与缺盖样图的模板匹配区分。
- 目录相机按文件名顺序读取支持的图片。
- 相机、任务和检测记录的数据库往返。
- 视觉结果映射为稳定运行时数据点。
- 缺盖结果进入实时库、触发动态报警规则并写入历史库的完整链路。

运行命令：

```powershell
dotnet test tests/IndustrialDAQ.Tests/IndustrialDAQ.Tests.csproj --no-restore
dotnet build src/IndustrialDAQ.UI/IndustrialDAQ.UI.csproj --no-restore
```

## 7. MVP 后续扩展

- 增加 GenICam、GigE Vision 或厂商 SDK 相机驱动。
- 增加画布拖拽式 ROI，而不是只输入归一化数值。
- 增加多模板、角度容差、亮度校正和产品配方切换。
- 增加 ONNX 缺陷检测算法并保留同一 `IVisionInspectionAlgorithm` 接口。
- 增加 NG 样本库、历史分页、图片复核和 CSV 报表导出。
