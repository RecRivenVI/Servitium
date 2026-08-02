# 阶段 11：Domain、Application 与 Contracts

## 1. 阶段目的

实现与基础设施无关的核心：领域模型与不变量、用例编排与端口接口、对外契约类型。此后所有阶段都以这三层为基础。

## 2. 需求编号

* `REQ-PLT-001` 标识与时间
* `REQ-PLT-021` 幂等（契约与用例层）
* `REQ-PLT-022` 分页（契约层）
* `REQ-IDN-030` 角色与实例级授权（领域与策略）
* `REQ-INS-040` … `REQ-INS-043` 实例状态机
* `REQ-INS-014`、`REQ-INS-015` 删除登记与删除工作目录的领域规则
* `REQ-USR-004` Owner 不可删除/降级/禁用
* `REQ-SCH-023`、`REQ-SCH-030`、`REQ-SCH-031`、`REQ-SCH-043` 计划任务的领域规则
* `REQ-HMC-010` Release 不可变
* `REQ-I18N-001` … `REQ-I18N-004` 文案资源来源

## 3. 前置条件

* 阶段 10 完成条件全部满足。

## 4. 范围

* `Servitium.Domain`：全部实体、值对象、枚举、状态机、领域规则与领域事件；
* `Servitium.Application`：用例接口与实现骨架、端口接口、授权策略、输入校验、映射；
* `Servitium.Contracts`：DTO、事件信封与载荷、界面契约文件、文案资源来源文件、protobuf 定义；
* 三者的单元测试。

## 5. 非目标

* 不实现任何持久化、网络、进程或文件系统访问；
* 不实现 REST 端点或 SignalR Hub；
* 不实现 Agent 侧逻辑；
* 用例实现中对端口的调用可以是完整的，但端口的具体实现留到阶段 12–17。

## 6. 需要创建的项目或目录

在既有项目下新增目录：

```text
src/Servitium.Domain/
├── Identity/          User、Role、Session、ApiKey、ExternalIdentity、WindowsIdentityBinding
├── Nodes/             Node、NodeCredential、NodeRegistrationToken、NodeStatus
├── Instances/         Instance、InstanceStatus、WorkingDirectory、LaunchConfiguration、StopConfiguration、AutoStartPolicy、TerminalSettings、RconSettings、QuerySettings
├── Java/              JavaRuntime、JavaRuntimeSource、JavaArchitecture
├── Scheduling/        ScheduledTask、TriggerKind、ScheduleAction、OverlapPolicy、TaskRun
├── Files/             FilePath（值对象）、PathValidationResult
├── Distribution/      HmclChannel、HmclRelease、HmclReleaseFile、ChannelVisibility、ContentHash
├── Operations/        Operation、OperationStatus、OperationKind
├── Auditing/          ActivityLogEntry、AuditLogEntry、AuditCategory
├── Common/            Entity、ValueObject、DomainEvent、DomainException、Guard
└── Settings/          SystemSettings 的领域约束

src/Servitium.Application/
├── Abstractions/      端口接口
├── Identity/          用例
├── Nodes/
├── Instances/
├── Java/
├── Files/
├── Configurations/
├── Mods/
├── Scheduling/
├── Distribution/
├── Operations/
├── Auditing/
├── Settings/
├── Authorization/     授权策略与判定
└── Common/            结果类型、分页、验证

src/Servitium.Contracts/
├── Identity/          DTO
├── Nodes/
├── Instances/
├── Java/
├── Files/
├── Configurations/
├── Mods/
├── Scheduling/
├── Distribution/
├── Operations/
├── Auditing/
├── Settings/
├── Events/            事件信封与载荷
├── Errors/            ErrorCodes（阶段 10 已建）
├── Ui/                界面契约文件
├── Localization/      zh-Hans.json、en-US.json
└── Protos/            agent-control.proto
```

## 7. 需要实现的类型或模块

### 7.1 Domain 关键类型

| 类型 | 不变量 |
| --- | --- |
| `User` | 用户名唯一且非空；`Owner` 不可删除、不可降级、不可禁用；系统中恒有且仅有一个 `Owner` |
| `Role` | `Owner`、`Administrator`、`User`、`Disabled` |
| `Instance` | 名称在节点内唯一；状态转换必须合法；工作目录非空且为绝对路径；`OwnedByServitium` 一经确定只能通过修改工作目录改变 |
| `InstanceStatus` | `Stopped`、`Starting`、`Running`、`Stopping`、`Busy`、`Crashed`、`Unreachable` |
| `WorkingDirectory` | 值对象：绝对路径 + `OwnedByServitium`；提供 `CanDeleteRecursively` 判定 |
| `LaunchConfiguration` | 结构化模式与原始命令模式二选一；原始命令的首参数必须引用已登记的 Java 运行时；堆大小 min ≤ max |
| `Node` | 显示名唯一；状态 `Online`/`Offline`/`Incompatible`/`Revoked` |
| `JavaRuntime` | 架构必须为 x64 或 arm64；来源为 `Discovered`/`Manual`/`Imported`；仅 `Imported` 允许删除文件 |
| `ScheduledTask` | 名称在实例内唯一；动作数 1–10；间隔 ≥ 10 秒；剩余次数 -1 或 ≥ 1；每实例任务数 ≤ 上限 |
| `HmclRelease` | 创建后不可修改；文件清单非空路径与 40 位小写十六进制哈希；游戏版本必填；加载器至多一个模组加载器 + 可选 OptiFine |
| `HmclChannel` | slug 唯一、`^[a-z0-9-]{3,48}$`、创建后不可改 |
| `Operation` | 状态转换 `Pending → Running → (Succeeded|Failed|Cancelled)` |
| `AuditLogEntry` | 创建后不可修改（无任何 setter，无更新方法） |

### 7.2 实例状态机

```text
Stopped  --start-->  Starting --成功--> Running
Starting --失败-->   Stopped 或 Crashed
Running  --stop-->   Stopping --> Stopped
Running  --进程异常退出--> Crashed
Crashed  --自动重启--> Starting
任意状态 --force-stop--> Stopped
任意状态 --长操作占用--> Busy --完成--> 原状态
任意状态 --节点离线--> Unreachable --节点恢复--> 实际状态
```

非法转换抛 `InvalidInstanceStateTransitionException`，携带错误码 `instance-not-stopped` 或 `conflict`。

### 7.3 Application 端口接口

| 端口 | 用途 |
| --- | --- |
| `IClock` | 时间 |
| `IGuidFactory` | 标识生成 |
| `IPasswordHasher` | Argon2id |
| `ITotpService` | TOTP 生成与校验 |
| `ISecretProtector` | DPAPI 加解密 |
| `ITokenGenerator` | 高熵令牌 |
| `IUnitOfWork` | 事务边界 |
| `I*Repository` | 各聚合的仓储 |
| `IAgentGateway` | 向 Agent 下发指令与打开子流 |
| `IEventPublisher` | 发布实时事件 |
| `IOperationTracker` | 长操作生命周期 |
| `IContentStore` | HMCL 内容库 |
| `IActivityLogger` / `IAuditLogger` | 双日志流 |
| `IOutboundHttpClient` | 受 SSRF 约束的出站请求 |
| `IPathValidator` | 路径校验（Server 侧的预校验） |

### 7.4 Application 授权

单一入口 `IAuthorizationService`：

```text
Authorize(principal, resource, action) -> Allowed | Denied(code)
```

`resource` 覆盖：全局、节点、实例、用户、Channel、操作。`action` 为动作枚举。全部端点与 Hub 方法必须经过它（架构测试守护）。

### 7.5 Contracts

* DTO 与请求响应模型，命名与 `protocols-and-contracts.md` 一致；
* 事件信封 `EventEnvelope` 与全部事件载荷类型；
* 界面契约文件：页面编号、路由、权限、区块顺序、字段键、动作键、状态键、文案键、是否平台专属；
* 文案资源来源文件：`zh-Hans.json`、`en-US.json`，键命名遵循 `localization-specification.md` 第 4 节；
* protobuf：按 `protocols-and-contracts.md` 第 6 节定义 `AgentControl` 服务、消息与全部指令类型。

## 8. 数据模型

领域模型定义在本阶段完成；持久化映射在阶段 12 完成。字段与约束见 `docs/architecture/data-and-storage.md` 第 3 节。

## 9. API 或事件

* 事件类型的**契约定义**在本阶段完成（`Contracts/Events`），推送实现在阶段 12；
* protobuf 定义在本阶段完成，实现在阶段 12–13。

## 10. 实施顺序

1. `Domain/Common`：`Entity`、`ValueObject`、`DomainEvent`、`DomainException`、`Guard`；
2. `Domain/Identity`：用户、角色、会话、API Key、外部身份、Windows 绑定；
3. `Domain/Nodes`；
4. `Domain/Java`；
5. `Domain/Instances`（含状态机与启动配置）；
6. `Domain/Scheduling`；
7. `Domain/Distribution`；
8. `Domain/Operations`、`Domain/Auditing`、`Domain/Settings`、`Domain/Files`；
9. `Contracts`：DTO、事件、错误码补全；
10. `Contracts/Protos`：protobuf 定义；
11. `Contracts/Localization`：两种语言的资源来源文件（本阶段登记已知的键，后续阶段增补）；
12. `Contracts/Ui`：界面契约文件（按 `information-architecture.md` 全量登记）；
13. `Application/Abstractions`：端口接口；
14. `Application/Authorization`：授权策略与判定；
15. `Application/*`：各域用例（调用端口，端口暂无实现）；
16. `Application/Common`：结果类型、分页、验证；
17. 单元测试；
18. 扩展 `eng/Generate.ps1` 处理 protobuf 与文案资源生成；
19. 运行 `eng/Validate.ps1`。

## 11. 关键算法

| 算法 | 说明 |
| --- | --- |
| 实例状态转换 | 见 7.2；实现为纯函数 + 实体方法，禁止在外部直接赋值状态 |
| 启动参数生成 | 从 `LaunchConfiguration` 生成参数数组：`[java.exe, -Xms<min>m, -Xmx<max>m, ...额外JVM参数, (-jar <jar> \| @<argfile>), ...服务端参数]`；顺序固定；不经 shell |
| 授权判定 | 角色优先：`Owner`/`Administrator` 直通（Owner 专属动作除外）；`User` 需实例授权且动作在允许集合内；`Disabled` 一律拒绝 |
| 计划任务下次触发时间 | 间隔：上次触发 + 间隔；日历：按节点本地时区求下一个匹配的「星期 × 时刻」；指定：固定时间点 |
| 分页游标编码 | 由排序键与标识组成的不透明字符串（Base64URL 编码的稳定结构），保证在数据变动下不重不漏 |

## 12. Windows 平台事项

`Domain` 与 `Application` 不含平台特定代码。路径值对象只做与平台无关的形态校验（非空、绝对路径形态、无控制字符）；完整的 Windows 路径安全校验在 `Infrastructure` 与 Agent 侧实现（阶段 13、15）。

## 13. 安全要求

* 领域层强制 Owner 不变量（`REQ-USR-004`）；
* 审计实体无任何修改路径（`REQ-SEC-080` 的领域基础）；
* 授权判定集中在单一服务；
* 启动配置的原始命令模式在领域层校验首参数必须引用已登记运行时（`REQ-INS-081`）；
* 文案资源中不得包含任何秘密或内部路径。

## 14. 自动化测试

单元测试（分类 `Unit`），覆盖率 `Domain` 与 `Application` ≥ 85%：

| 编号族 | 内容 |
| --- | --- |
| `UNT-100` | 实例状态机：全部合法转换成功；全部非法转换抛异常并携带正确错误码 |
| `UNT-110` | Owner 不变量：删除、降级、禁用均抛异常 |
| `UNT-120` | 启动参数生成：至少 10 组配置组合的精确参数数组比对 |
| `UNT-130` | 原始命令校验：首参数非已登记运行时时被拒 |
| `UNT-140` | 工作目录：`OwnedByServitium=false` 时 `CanDeleteRecursively` 为假 |
| `UNT-150` | 计划任务不变量：动作数、间隔下限、剩余次数、名称唯一 |
| `UNT-160` | 下次触发时间计算：三种触发方式各覆盖跨日、跨周、夏令时无关性 |
| `UNT-170` | Release 不可变：任何修改尝试抛异常；哈希与路径格式校验 |
| `UNT-180` | Channel slug 校验 |
| `UNT-190` | 授权判定矩阵：角色 × 动作 × 资源的完整真值表 |
| `UNT-200` | 分页游标：编码解码往返、数据变动下不重不漏 |
| `UNT-210` | 长操作状态转换 |

架构测试新增：

| 编号 | 断言 |
| --- | --- |
| `ARC-020` | `Domain` 中不存在 `DateTime` 类型的公共属性或字段 |
| `ARC-021` | `AuditLogEntry` 无公共 setter 且无 `Update`/`Delete` 方法 |
| `ARC-022` | `Application` 中所有用例都注入 `IAuthorizationService`（或显式标注为无需授权，如登录） |
| `ARC-023` | `Contracts` 中的事件类型与 `protocols-and-contracts.md` 4.3 节的表格一致 |
| `ARC-024` | `Contracts/Ui` 的界面契约覆盖 `information-architecture.md` 第 1 节的全部页面编号 |

## 15. 集成测试

本阶段无集成测试（无基础设施实现）。

## 16. 人工测试

无。

## 17. 调试启动方式

```powershell
./eng/Test.ps1 -Category Unit
./eng/Test.ps1 -Category Architecture
```

## 18. 验收责任方

编码 Agent 自主验收。

## 19. 完成条件

1. `Domain`、`Application`、`Contracts` 三个项目编译无警告；
2. `UNT-100` … `UNT-210` 全部通过；
3. `ARC-001` … `ARC-024` 全部通过；
4. `Domain` 与 `Application` 行覆盖率 ≥ 85%；
5. protobuf 生成成功且 `Generate.ps1 -Verify` 通过；
6. 文案资源两种语言键集合一致（本阶段已登记的键）；
7. 界面契约文件覆盖全部页面编号；
8. `eng/Validate.ps1` 全量通过。

## 20. 禁止提前进入下一阶段的条件

* 实例状态机未实现或未测试；
* 授权判定未集中到单一服务；
* Owner 不变量未在领域层强制；
* 覆盖率未达阈值；
* 界面契约或事件契约与规格文档不一致。

## 21. 需要更新的动态记录

* `docs/records/current-status.md`
* `docs/records/development-log.md`
* `docs/records/decision-log.md`（记录值对象设计、结果类型选择等实现级决策）
* `docs/records/validation-log.md`
* `docs/testing/requirements-traceability.md`（登记本阶段覆盖的需求）
* `docs/testing/acceptance-matrix.md`
