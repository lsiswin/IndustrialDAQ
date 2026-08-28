# 用户权限与 ResourcePath 动态授权说明

## 1. 设计目标

本系统面向工业上位机运行环境，权限设计遵循最小权限、默认拒绝、职责分离、拒绝优先和关键操作可追溯原则。账号身份只负责说明“谁在操作”，`ResourcePath + Action` 决定“可以对哪个现场资源执行什么操作”。

## 2. 权限数据模型

| 表 | 作用 |
|---|---|
| `security_users` | 保存账号、PBKDF2 密码摘要、启用状态、失败次数、锁定时间和首次改密标记 |
| `security_roles` | 保存动态角色定义 |
| `security_user_roles` | 保存用户与角色的多对多关系 |
| `resource_nodes` | 保存工厂、区域、产线、设备和数据点组成的资源树 |
| `permission_policies` | 保存用户、角色、组或系统主体针对资源路径的权限策略 |
| `security_audits` | 保存登录、授权拒绝、权限变更和数据写入等安全审计事件 |

账号与角色已经完全拆表，不再在用户表中保存逗号分隔角色。运行时从 `security_user_roles` 关联加载角色，并构造 `PermissionSubject`。

## 3. 内置角色

- `Guest`：未登录或注册后的默认身份，只查看，不授予数据写入权限。
- `Operator`：预留给生产操作员，可通过策略按产线或设备授予有限操作权限。
- `Engineer`：默认允许写入 `Devices` 下的设备数据点，但可被更具体的拒绝策略覆盖。
- `Admin`：默认允许管理 `Devices` 和 `System` 下的资源，负责账号和权限策略维护。

角色名称只是授权主体，不在业务页面中写死具体设备权限。现场可通过 `permission_policies` 动态缩小或扩展角色的资源范围。

## 4. ResourcePath 资源树

采集配置启动后，`ResourceTreeSynchronizer` 将实际设备同步为统一树结构：

```text
Devices
└── {设备名称}
    └── {数据点名称}
```

每个资源节点具有稳定的 `Id`、层级化 `ResourcePath`、资源类型和元数据。数据写入、报警规则、趋势监控和权限策略共享同一资源路径，不再分别拼接互不兼容的地址。

资源路径会统一分隔符、移除首尾斜杠并拒绝空路径和路径内通配符。对子树授权使用策略的 `Inherit` 字段，而不是在路径字符串中嵌入通配符。

## 5. 权限策略结构

一条 `PermissionPolicy` 包含以下核心字段：

- `SubjectType`：`User`、`Role`、`Group` 或 `System`。
- `SubjectId`：用户 ID、角色名称、组 ID 或系统主体。
- `ResourcePath`：策略作用的资源节点。
- `Action`：如 `Read`、`Write`、`ManagePermission`，`*` 表示所有动作。
- `Effect`：`Allow` 或 `Deny`。
- `Inherit`：是否应用到所有后代资源。
- `Priority`：同层级同效果策略的排序依据。
- `IsEnabled`、`Version`：用于停用和热更新版本控制。

策略必须通过 `PermissionManagementService` 保存或停用。该服务完成数据库写入后调用 `AuthorizationService.ReloadAsync()`，用新的不可变快照原子替换旧快照，因此运行时不需要重启。

## 6. 授权判定流程

```text
用户登录
  → 从用户角色关联表加载角色
  → 构造 PermissionSubject（用户 + 角色 + 组）
  → 页面发起 ResourcePath + Action 请求
  → 在不可变 PermissionSnapshot 中筛选主体、动作和路径
  → 精确路径或 Inherit 后代匹配
  → Deny 优先，其次路径深度，再次策略 Priority
  → 有 Allow 则放行；无匹配策略默认拒绝
  → 执行业务操作并写入审计日志
```

当前判定采用全局拒绝优先：只要候选策略中存在匹配的 `Deny`，即使另有更具体的 `Allow` 也会拒绝。这种语义适合急停、安全联锁和受限设备等工业安全场景。

## 7. 数据写入保护

设备详情页和生产监控页现在使用相同流程：

1. 未登录、访客或只读账号不显示/不启用修改入口。
2. 命令执行时再次校验身份，防止只依赖 UI 隐藏。
3. 根据数据点 ID 从资源树取得权威 `ResourcePath`。
4. 调用 `CanAsync(subject, resourcePath, "Write")`。
5. 拒绝时不调用设备驱动，并记录 `AuthorizationDenied`。
6. 放行后调用协议驱动；成功和失败均记录 `TagWrite`。

即使权限表异常为空，系统也执行默认拒绝，不再采用“空策略全局放行”。数据库初始化会创建工程师和管理员的基础策略，保证安全默认值与正常维护能力兼顾。

## 8. 审计日志

审计记录包含 UTC 时间、用户 ID、账号、动作、资源路径、详情和成功状态。当前覆盖：

| 动作 | 触发条件 |
|---|---|
| `Login` | 登录成功 |
| `LoginFailed` | 密码错误或账号不可用 |
| `Logout` | 已登录用户主动退出 |
| `PermissionChanged` | 管理员修改账号角色或启用状态 |
| `PermissionPolicySaved` | 管理员新增或修改资源权限策略 |
| `PermissionPolicyDisabled` | 管理员停用资源权限策略 |
| `AuthorizationDenied` | 数据写入或权限维护被拒绝 |
| `TagWrite` | 数据点写入成功或驱动写入失败 |

系统设置的“用户与权限管理中心”可查看最近 200 条安全审计记录。数据库按时间、用户和动作建立索引，便于现场追溯。

## 9. 本次验证结果

执行日期：2026-08-28。

- WPF 全项目编译：通过，0 个错误；存在 8 个原有可空引用警告。
- 父路径权限继承：通过。
- 更具体资源的拒绝策略优先：通过。
- 无匹配策略默认拒绝：通过。
- 审计日志写入和读取：通过。
- 审计日志倒序及失败状态：通过。
- 审计资源路径及成功状态：通过。
- 实际客户端数据库初始化：通过。
- 安全表、4 个内置角色、3 条基础动态策略和审计索引：均已验证存在。

## 10. 后续增强建议

- 为权限策略增加系统设置中的可视化编辑器，按资源树选择节点，避免人工输入路径。
- 实现并校验 `ConditionJson`，支持班次、工位、设备状态和维护窗口等条件授权；在实现前该字段不应产生授权效果。
- 为审计日志增加日期、账号、动作、资源路径和结果筛选以及导出功能。
- 增加审计归档、保留周期和防篡改校验，满足长期运行与合规要求。
- 将报警确认、规则变更、设备配置变更、用户创建、密码重置和首次改密纳入统一审计事件目录。

## 11. 主要代码入口

- `IndustrialDAQ.Core/Authorization/AuthorizationService.cs`：运行时授权判定与默认拒绝。
- `IndustrialDAQ.Core/Authorization/PermissionSnapshot.cs`：策略匹配、继承、拒绝优先和排序。
- `IndustrialDAQ.Core/ResourceTree/ResourceTreeSynchronizer.cs`：设备配置到资源树的同步。
- `IndustrialDAQ.UI/Services/PermissionManagementService.cs`：策略唯一维护入口和热重载。
- `IndustrialDAQ.UI/Services/SecurityAuditService.cs`：安全审计持久化与查询。
- `IndustrialDAQ.UI/ViewModels/DeviceDetailViewModel.cs`：设备详情数据写入授权。
- `IndustrialDAQ.UI/ViewModels/ProductionMonitorViewModel.cs`：生产监控数据写入授权。
- `IndustrialDAQ.UI/ViewModels/UserAccessManagementViewModel.cs`：账号权限和审计日志展示。
