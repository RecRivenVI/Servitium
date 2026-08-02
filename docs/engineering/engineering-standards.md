# 工程规范

本文件定义 Servitium 的编码、结构、命名与质量规范。全部条目为强制要求，由分析器、lint 规则、架构测试或 `eng/Validate.ps1` 守护。

## 1. .NET

### 1.1 语言与编译

| 项 | 规范 |
| --- | --- |
| 目标框架 | .NET 10 |
| 语言版本 | `latest` |
| 可空引用类型 | 全项目启用（`<Nullable>enable</Nullable>`） |
| 隐式 using | 关闭；显式引入 |
| 警告即错误 | 启用（`TreatWarningsAsErrors`） |
| 分析器级别 | `latest`，`AnalysisMode=All` |
| 确定性构建 | 启用；`ContinuousIntegrationBuild` 在 CI 中启用 |
| 不安全代码 | 仅 `Servitium.Infrastructure` 中的 Windows 互操作允许，且需集中在 `Interop/` 目录 |
| XML 文档 | 公共 API 必须有；内部实现按需 |

### 1.2 命名

遵循 .NET 常规命名，另加：

| 类别 | 规范 |
| --- | --- |
| 接口 | `I` 前缀 |
| 抽象类 | 不加前缀 |
| 异步方法 | `Async` 后缀 |
| 端口接口 | 以能力命名（`IContentStore`、`IProcessLauncher`），不以实现命名 |
| DTO | `<用途>Request` / `<用途>Response` / `<实体>Dto` |
| 领域事件 | 过去式（`InstanceStarted`） |
| 命令与查询 | `<动作><对象>Command` / `<对象>Query` |
| 常量类 | `<领域>Constants` |
| 扩展方法类 | `<被扩展类型>Extensions` |
| 测试类 | `<被测类型>Tests` |
| 测试方法 | `方法名_场景_期望` |

### 1.3 结构

| 规范 | 说明 |
| --- | --- |
| 一个文件一个顶级类型 | 部分类与嵌套类型除外 |
| 文件名等于类型名 | 泛型用 `Type`+`` `N `` 的基名 |
| 文件作用域命名空间 | 强制 |
| 命名空间与目录一致 | 强制 |
| `using` 排序 | `System` 优先，其余字母序，分组间空行 |
| 成员顺序 | 常量 → 字段 → 构造 → 属性 → 方法 → 嵌套类型；同类别内 public → internal → private |

### 1.4 编码约定

| 约定 | 说明 |
| --- | --- |
| 显式类型 | 除非右侧类型显而易见（`new()`、字面量、强制转换），否则不使用 `var` |
| 不可变优先 | 字段默认 `readonly`；DTO 使用 `record` 或 `init` 属性 |
| 构造注入 | 依赖通过构造函数注入；不使用服务定位器 |
| 取消 | 所有可能阻塞的异步方法接受 `CancellationToken` 并向下传递 |
| 异步 | 不使用 `.Result`、`.Wait()`、`.GetAwaiter().GetResult()`；库代码使用 `ConfigureAwait(false)` |
| 异常 | 领域错误用领域异常；不用异常表达正常控制流；不吞异常 |
| 日志 | 结构化日志，使用命名占位符；不拼接字符串；不记录秘密 |
| 时间 | 通过 `IClock` 获取；不直接调用 `DateTimeOffset.UtcNow`（除 `IClock` 实现内） |
| 随机 | 安全相关使用 `RandomNumberGenerator`；其余通过 `IRandom` 端口 |
| 路径 | 统一经路径校验服务；不直接拼接字符串路径 |
| 进程 | 统一经 `IProcessLauncher`；不直接 `Process.Start` |
| 文件 IO | 统一经 `IFileSystem` 端口，便于测试替身 |
| LINQ | 避免在热路径中多次枚举；显式物化 |
| 集合 | 公共 API 返回 `IReadOnlyList<T>` / `IReadOnlyDictionary<>` |

### 1.5 禁止

| 禁止 | 理由 |
| --- | --- |
| `async void`（事件处理器除外） | 异常不可捕获 |
| `Thread.Sleep` | 使用 `Task.Delay` |
| `lock` 于公开对象 | 使用私有锁对象 |
| 静态可变状态 | 不可测试；用依赖注入 |
| 反射用于业务逻辑 | 仅允许于序列化与测试基础设施 |
| `#pragma warning disable` 无理由 | 必须附注释说明原因 |
| 直接 SQL 字符串 | 使用 EF Core；确需原生 SQL 时参数化并集中管理 |
| 在 `Domain` 引用框架类型 | 破坏分层 |

## 2. 依赖与包管理

| 项 | 规范 |
| --- | --- |
| 版本管理 | 中央包版本管理（`Directory.Packages.props`） |
| 锁定文件 | 启用 `RestorePackagesWithLockFile`；CI 使用 `--locked-mode` |
| 包源映射 | 启用，防止依赖混淆 |
| 漏洞扫描 | `dotnet list package --vulnerable --include-transitive` 纳入验证入口；有已知高危漏洞即失败 |
| 新增依赖 | 需在 `docs/records/decision-log.md` 中登记：用途、许可、维护状态、替代方案 |
| 许可 | 仅接受 MIT、Apache-2.0、BSD、MS-PL 等宽松许可；GPL/AGPL 类不得引入 |

## 3. TypeScript 与 React

### 3.1 语言

| 项 | 规范 |
| --- | --- |
| `strict` | 全开 |
| `noUncheckedIndexedAccess` | 开启 |
| `exactOptionalPropertyTypes` | 开启 |
| `any` | 禁止；确需时用 `unknown` 并做收窄 |
| 非空断言 `!` | 禁止（生成代码除外） |
| 枚举 | 使用联合字面量类型，不使用 `enum` |
| 导出 | 命名导出优先；页面组件可用默认导出 |

### 3.2 React

| 项 | 规范 |
| --- | --- |
| 组件 | 函数组件 |
| Hooks | 遵循 Hooks 规则（lint 强制） |
| 副作用 | `useEffect` 必须有明确的依赖数组与清理 |
| 状态 | 服务端状态归查询层；不复制到本地 state |
| 渲染 | 避免在渲染中创建新对象/函数导致的无谓重渲染（对热点列表） |
| 键 | 列表 `key` 使用稳定标识，不用索引 |
| 错误边界 | 每个路由级页面包裹错误边界 |

### 3.3 命名

| 类别 | 规范 |
| --- | --- |
| 组件文件与组件名 | `PascalCase` |
| hooks | `useXxx`，文件同名 |
| 其他模块文件 | `kebab-case` 或与默认导出同名的 `PascalCase` |
| 目录 | 小写 |
| 常量 | `SCREAMING_SNAKE_CASE` |
| 类型与接口 | `PascalCase`，不加 `I` 前缀 |

### 3.4 禁止

见 `docs/design/web-specification.md` 第 15 节。

## 4. 数据库

| 项 | 规范 |
| --- | --- |
| ORM | EF Core |
| 迁移 | 每次结构变更一个迁移；迁移名 `<日期>_<简述>` |
| 命名 | 表名复数 `PascalCase`；列名 `PascalCase`；索引 `IX_<表>_<列...>`；外键 `FK_<表>_<引用表>_<列>` |
| 主键 | `Guid`（`Id`）；审计与日志类表允许自增 `long` 作为顺序键 |
| 并发 | 需要乐观并发的实体使用 `RowVersion` 列 |
| 软删除 | 不使用；删除即删除，历史通过日志保留 |
| 查询 | 读路径默认 `AsNoTracking()` |
| N+1 | 显式 `Include` 或投影；有专门测试守护热点查询的往返次数 |
| 原生 SQL | 集中在 `Infrastructure/Persistence/RawQueries`，参数化 |

## 5. gRPC 与 protobuf

| 项 | 规范 |
| --- | --- |
| 文件位置 | `src/Servitium.Contracts/Protos/` |
| 包名 | `servitium.agent.v1` |
| 命名 | 消息 `PascalCase`；字段 `snake_case`；枚举值 `SCREAMING_SNAKE_CASE` 且带类型前缀 |
| 字段编号 | 一经分配不可复用；删除字段用 `reserved` |
| 兼容性 | 只新增可选字段；不改类型、不改编号、不改语义 |
| 大消息 | 超过 1 MiB 的数据使用流而非单条消息 |

## 6. API 设计

| 项 | 规范 |
| --- | --- |
| 资源命名 | 复数名词、`kebab-case`（`java-runtimes`） |
| 动作 | 非 CRUD 动作作为子资源的 POST（`/start`、`/force-stop`） |
| 幂等 | GET/PUT/DELETE 幂等；POST 通过 `Idempotency-Key` |
| 状态码 | 见 `docs/architecture/protocols-and-contracts.md` |
| 字段命名 | `camelCase` |
| 枚举 | 字符串字面量，`kebab-case` |
| 分页 | 游标分页 |
| 版本 | 路径版本 |

## 7. 测试规范

| 项 | 规范 |
| --- | --- |
| 命名 | `方法名_场景_期望` |
| 结构 | Arrange / Act / Assert，段间空行 |
| 断言 | 每个测试聚焦一个行为；允许多条断言但必须围绕同一行为 |
| 隔离 | 测试之间不共享可变状态；使用独立的临时目录与数据库文件 |
| 时间 | 通过 `IClock` 的测试实现控制；不使用真实等待 |
| 随机 | 固定种子 |
| 外部依赖 | 单元测试用替身；集成测试用真实资源 |
| 不确定性 | 禁止 `Thread.Sleep` 等待；使用轮询 + 超时的显式等待助手 |
| 跳过 | 禁止 `Skip`、条件忽略或把未执行记为通过；缺少平台、权限或环境时测试整体失败并进入阻塞流程 |
| 覆盖率 | `Domain` 与 `Application` 行覆盖率不低于 85%；整体不低于 70%。覆盖率不作为唯一质量指标，但低于阈值即失败 |

### 7.1 测试分类标记

| 标记 | 含义 | 在验证入口中的阶段 |
| --- | --- | --- |
| `Unit` | 无 I/O | 单元测试 |
| `Integration` | 真实数据库或文件系统 | 集成测试 |
| `Contract` | 契约一致性 | 契约测试 |
| `Security` | 安全负向测试 | 安全测试 |
| `Architecture` | 分层与约束 | 架构测试 |
| `Ui` | WinUI 自动化 | UI 测试 |
| `EndToEnd` | 全链路 | 端到端测试 |
| `Capacity` | 容量与压力 | 容量测试 |
| `RequiresElevation` | 需要管理员权限 | 单独阶段，缺权限时**阻塞而非跳过** |

## 8. 日志规范

| 级别 | 用途 |
| --- | --- |
| `Trace` | 详细诊断，默认关闭 |
| `Debug` | 开发期诊断 |
| `Information` | 生命周期事件、重要状态变化 |
| `Warning` | 可恢复的异常情况 |
| `Error` | 操作失败 |
| `Critical` | 服务不可用 |

要求：

* 结构化字段：`traceId`、`userId`、`nodeId`、`instanceId`、`operationId`（存在时必带）；
* 消息模板使用命名占位符，不拼接；
* 不记录秘密（由脱敏中间层强制）；
* 服务日志固定 `en-US`；
* 日志文件按天滚动，保留期见 `docs/architecture/data-and-storage.md`。

## 9. 配置

| 项 | 规范 |
| --- | --- |
| 来源 | `appsettings.json`（默认）→ `appsettings.<环境>.json` → 环境变量（`SERVITIUM_` 前缀）→ 命令行 |
| 绑定 | 强类型选项类 + 启动时校验（`ValidateOnStart`），校验失败即拒绝启动 |
| 秘密 | 不写入明文配置；使用 DPAPI 加密的独立文件 |
| 运行时可变设置 | 存数据库（`SystemSetting`），不存配置文件 |
| 环境 | `Development`、`Staging`、`Production`；测试使用 `Testing` |

## 10. 错误处理

| 层 | 规范 |
| --- | --- |
| `Domain` | 抛领域异常，携带稳定错误码 |
| `Application` | 捕获端口异常并转换为带错误码的应用异常；不泄露实现细节 |
| `Server` | 统一异常中间件转换为 Problem Details；未预期异常记录 `Error` 并返回 `internal-error` |
| `Agent` | 指令处理捕获异常并返回带错误码的失败响应；不让异常终止连接 |
| 客户端 | 按 `code` 分支；未知 `code` 回退到通用文案 |

## 11. 并发

| 场景 | 规范 |
| --- | --- |
| 数据库写 | 单写入队列串行化 |
| 实例状态转换 | 每实例一把锁；状态机拒绝非法转换 |
| 文件任务 | 节点级信号量限制并发 |
| 终端输出扇出 | 每订阅者独立的有界队列；满时丢弃最旧并插入标记 |
| gRPC 流 | 每子流独立的背压窗口 |
| 后台作业 | 使用托管的后台服务；关机时优雅取消 |

## 12. Windows 互操作

| 项 | 规范 |
| --- | --- |
| 位置 | 集中在 `Servitium.Infrastructure/Interop/` |
| 方式 | 优先使用 `LibraryImport`（源生成）；必要时 `DllImport` |
| 句柄 | 使用 `SafeHandle` 派生类型 |
| 错误 | 检查返回值并用 `Marshal.GetLastWin32Error()` 构造异常 |
| 覆盖范围 | ConPTY、Job Object、命名管道 ACL、进程令牌与 SID、DPAPI、注册表（可用托管 API 时优先托管） |
| 测试 | 每个互操作封装必须有集成测试 |

## 13. 文档与注释

| 项 | 规范 |
| --- | --- |
| 注释语言 | 中文 |
| 注释内容 | 解释「为什么」，不复述「做了什么」 |
| TODO | 禁止提交未登记的 TODO；确需时必须写成 `// TODO(<问题编号>): ...` 并在 `docs/records/problem-log.md` 中有对应条目 |
| 公共 API | XML 文档注释 |
| 复杂算法 | 在实现处引用对应的规格文档章节 |

## 14. 提交与变更

见 `AGENTS.md`。要点：

* 默认不得提交；
* 获得授权后按独立、可验证单元提交；
* 提交前必须通过对应测试；
* 提交信息只能包含单行标题，禁止正文、脚注与 trailer；
* 标题使用 `type(scope): 中文摘要`：`type` 取 `AGENTS.md` 规定的 11 个英文词，`scope` 必填并优先取八个宏观中文范围中的一个，结构标点使用 ASCII 半角形式，中文摘要中的标点使用中文全角形式。

## 15. 规范的强制手段

| 规范类别 | 强制手段 |
| --- | --- |
| C# 格式与命名 | `.editorconfig` + `dotnet format --verify-no-changes` |
| C# 质量 | 分析器 + `TreatWarningsAsErrors` |
| 分层依赖 | 架构测试 |
| TypeScript 格式 | Prettier |
| TypeScript 质量 | ESLint（`--max-warnings=0`） |
| 生成代码一致性 | 生成到独立临时目录后，目标文件集合与逐字节内容必须和仓库产物一致 |
| 本地化完备性 | 本地化校验脚本 |
| 依赖漏洞 | `dotnet list package --vulnerable` + `npm audit` |
| 包体积 | 构建报告与预算 |
| 覆盖率 | 覆盖率门禁 |

全部手段在 `eng/Validate.ps1` 中串联，见 `build-and-validation.md`。
