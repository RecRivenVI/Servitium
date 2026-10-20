# 系统架构

## 1. 运行拓扑

```text
Servitium.Desktop ─┐
                   ├─ REST / SignalR / 本机换票 ─▶ Servitium.Server
浏览器 ────────────┘                              │
                                                  │ gRPC (AgentControl.v1)
                                                  ▼
                                      一个或多个 Servitium.Agent
                                                  │
                                                  ▼
                                      Minecraft Java 服务端进程
```

HMCL 客户端独立于上述路径，直接访问 `Servitium.Server` 的 HMCL 分发端点：

```text
HMCL 客户端 ── HTTP GET ──▶ Servitium.Server /hmcl/**
```

### 1.1 连接方向

| 连接 | 发起方 | 说明 |
| --- | --- | --- |
| 浏览器 → Server | 浏览器 | HTTPS（非回环时强制） |
| Desktop → Server | Desktop | 同上；本机场景额外使用命名管道换票 |
| Agent → Server | **Agent** | Agent 主动出站建立 gRPC 双向流；Agent 不监听任何入站业务端口 |
| HMCL → Server | HMCL 客户端 | 只读 HTTP |

客户端**永不**直连 Agent（`DEC-D02`）。所有终端、文件流、指令均经控制平面中转。

### 1.2 单机部署

单机场景下 Server 与 Agent 安装在同一台机器上，Agent 通过 `127.0.0.1` 连接 Server。除连接地址外，代码路径与多机部署完全相同——不存在「本地模式」的特殊分支。

## 2. 职责划分

### 2.1 `Servitium.Server`

| 职责 | 说明 |
| --- | --- |
| 身份与会话 | 用户、密码、TOTP、API Key、OIDC、会话、CSRF |
| 授权 | 角色判定与实例级白名单，四条路径统一 |
| 节点登记 | 注册令牌、凭据签发与轮换、连接管理、心跳、协议协商 |
| 指令编排 | 把客户端请求翻译为 Agent 指令并聚合结果 |
| 实时事件 | 事件扇出与作用域过滤 |
| 终端中转 | 客户端 SignalR ↔ Agent gRPC 子流 |
| 文件流中转 | 上传分片与下载流的转发、限速与配额 |
| 长操作 | `OperationId` 的生命周期、进度、取消、持久化 |
| 审计与日志 | 操作日志与安全审计日志 |
| 系统设置 | 全局配置的读写与生效 |
| Web 托管 | 提供 `Servitium.Web` 的构建产物与 SPA 回退 |
| HMCL 分发 | Channel、Release、内容库、manifest 与 overrides 端点 |
| 仪表盘布局 | 按用户的布局存储 |

### 2.2 `Servitium.Agent`

| 职责 | 说明 |
| --- | --- |
| Java 运行时 | 探测、登记、导入、验证 |
| 实例生命周期 | 创建、启动、停止、重启、强制终止、崩溃识别、自动启停 |
| 进程与终端 | ConPTY、Job Object、输出缓冲、输入编码 |
| 文件 | 列表、增删改、上传落盘、下载读取、压缩解压、URL 下载 |
| 配置 | 识别、无损读写、校验、备份、原子保存 |
| 模组 | 扫描、元数据解析、启停、删除 |
| 计划任务 | **本地权威存储与调度** |
| 本地指标 | CPU、内存、磁盘、实例资源占用 |
| 本地自治 | 控制平面离线时实例与计划任务继续运行 |

### 2.3 `Servitium.Desktop`

只负责用户交互、托盘与通知。**不包含任何业务逻辑**，不直接访问数据库或文件系统（除本地设置与日志外），所有数据经 `Servitium.Client` 访问 Server。

### 2.4 `Servitium.Web`

React SPA，通过由 OpenAPI 生成的 TypeScript 客户端访问 Server。不手写 API 调用代码。

## 3. 项目分层

```text
Servitium.Domain          实体、值对象、状态机、领域规则、领域事件
Servitium.Application     用例、端口接口、编排、授权策略、验证
Servitium.Contracts       DTO、事件信封、错误码、界面契约、文案资源来源
Servitium.Infrastructure  端口实现：EF Core、文件系统、进程、ConPTY、加密、HTTP
Servitium.Client          .NET API 客户端（REST + SignalR），供 Desktop 使用
Servitium.Server          ASP.NET Core 组合根
Servitium.Agent           Windows 服务组合根
Servitium.Desktop         WinUI 3 组合根
Servitium.Web             React 应用
```

### 3.1 依赖方向

```text
Domain      ← Application ← Infrastructure ← Server
                  ↑                          ← Agent
Contracts ────────┘  ← Client ← Desktop
                     ← Web（经生成的 TS 客户端）
```

强制规则（由架构测试守护）：

| 规则 | 说明 |
| --- | --- |
| `Domain` 不引用任何其他 Servitium 项目 | 也不引用 EF Core、ASP.NET Core、任何 I/O 库 |
| `Application` 只引用 `Domain` 与 `Contracts` | 通过端口接口表达对外部资源的需要；使用独立协议契约，不引用基础设施 |
| `Contracts` 独立 | 不引用 `Domain`、`Application` 或任何基础设施 |
| `Infrastructure` 实现 `Application` 定义的端口 | 不被 `Domain` 或 `Application` 引用 |
| `Server` 与 `Agent` 是组合根 | 仅它们进行依赖注册与配置绑定 |
| `Client` 只依赖协议与 `Contracts` | 不引用 `Domain`、`Application`、`Infrastructure` |
| `Desktop` 只能通过 `Client` 访问 Server | 不引用 `Infrastructure` |
| `Web` 只能使用生成的 TypeScript API 客户端 | 不手写 `fetch` 调用 |

### 3.2 `Domain` 与 `Contracts` 的关系

两者都描述「实体的形状」，但用途不同：`Domain` 表达不变量与状态转换，随实现演进；`Contracts` 是对外承诺，变更受版本约束。二者之间通过 `Application` 层的映射转换，**不共享类型**。

## 4. Server 内部结构

```text
Servitium.Server
├── Endpoints/          REST 端点（按资源分组）
├── Hubs/               SignalR Hub（events、terminal）
├── AgentGateway/       gRPC 服务端：Agent 连接管理、指令分发、流复用
├── Hmcl/               HMCL 分发端点与内容库读取
├── Streaming/          文件上传分片与下载流中转
├── Operations/         长操作调度、进度、取消
├── Realtime/           事件扇出与作用域过滤
├── Security/           认证处理器、授权策略、CSRF、限流、本机管道
├── Hosting/            Windows 服务宿主、配置、健康检查
└── Web/                SPA 静态文件托管与回退
```

### 4.1 Agent 网关

Agent 连接后在网关中注册为一个会话。网关提供：

* **指令通道**：请求-响应型调用，带超时与取消；
* **流通道**：终端、文件上传、文件下载各占一类子流，按 `StreamId` 复用同一条 gRPC 双向流；
* **事件通道**：Agent 主动上报的状态变化、指标、日志、计划任务执行结果；
* **对账**：连接建立时交换实例、Java、计划任务的版本向量，差异写入日志并按「Agent 为准」原则同步。

### 4.2 长操作

长操作在 Server 侧建模为一条持久化记录（`Operations` 表），包含类型、目标、发起者、状态、进度、阶段、结果、错误。执行体可能在 Server（如 HMCL 发布）或 Agent（如实例创建）。Agent 侧长操作通过事件上报进度。

Server 重启后，处于「进行中」且执行体在 Agent 的操作会在 Agent 重连时对账；执行体在 Server 的操作被标记为「因服务重启而中断」。

## 5. Agent 内部结构

```text
Servitium.Agent
├── ControlPlane/       gRPC 客户端、重连、握手、指令分发
├── Instances/          实例注册表、状态机、生命周期
├── Processes/          ConPTY、Job Object、进程启动与监控
├── Terminals/          输出缓冲、订阅扇出、输入编码
├── Files/              文件操作、上传落盘、下载读取、归档
├── Configurations/     格式解析器、无损写回、备份
├── Mods/               扫描与元数据解析
├── Java/               探测、验证、导入
├── Scheduling/         本地调度器、执行历史
├── Metrics/            采样与上报
├── Storage/            本地 SQLite（实例、任务、历史、凭据引用）
└── Hosting/            Windows 服务宿主、关机处理
```

### 5.1 本地自治

Agent 的实例定义、Java 登记、计划任务定义与执行历史**权威副本在 Agent 本地**。控制平面保存镜像用于展示与编辑。

理由：控制平面不是单点（`DEC-D05`）。Server 停止时，Agent 必须能：

* 保持已运行的实例继续运行；
* 按计划任务定义继续触发；
* 在实例崩溃时按策略自动重启；
* 在系统关机时优雅停止实例。

重连后 Agent 上报期间发生的状态变化与执行历史，Server 补记入日志。

### 5.2 进程模型

每个实例对应：

* 一个 Windows Job Object（`KILL_ON_JOB_CLOSE`，禁止 breakaway）；
* 一个 ConPTY 伪终端（可关闭，关闭时使用重定向管道）；
* 一个读取输出的后台任务与一个环形缓冲；
* 一个进程退出监听。

Agent 进程本身持有 Job Object 句柄。Agent 异常终止时，操作系统关闭句柄并回收整棵进程树，因此不会遗留不可控的 Java 进程（`REQ-TRM-003`）。

Agent 正常停止或重启时，先按实例停止策略优雅关闭受管进程；进程退出或停止超时后关闭对应 Job Object，确保进程树被回收。Agent 恢复后根据实例的自动启动配置重新启动，不尝试接管旧进程。Agent 意外退出时同样由 `KILL_ON_JOB_CLOSE` 回收进程树，并在重新连接控制平面后上报中断与恢复结果。

## 6. Web 内部结构

```text
web/src
├── app/            应用外壳、路由、主题、错误边界
├── features/       按领域分的功能模块（instances、nodes、files、...）
├── shared/         跨领域组件、hooks、格式化、图标
├── api/            生成的 TypeScript 客户端（不手工编辑）
├── realtime/       SignalR 连接与订阅管理
└── i18n/           生成的文案资源
```

功能模块内部按 `components / hooks / model` 组织。模块之间不直接互相引用，共享内容上移到 `shared/`。

## 7. Desktop 内部结构

```text
Servitium.Desktop
├── App/            应用生命周期、单实例、主题、Mica
├── Shell/          NavigationView 外壳与导航
├── Views/          与 Web 页面一一对应的页面
├── ViewModels/     CommunityToolkit.Mvvm
├── Services/       会话、通知、托盘、深链接、设置
└── Strings/        生成的 .resw 文案资源
```

Desktop 不包含业务规则。数据获取、缓存与实时订阅由 `Servitium.Client` 提供。

## 8. 数据流示例

### 8.1 启动实例

```text
Web/Desktop
  → POST /api/v1/instances/{id}/start
  → Server: 授权校验 → 创建 Operation → 通过 AgentGateway 下发 StartInstance
  → Agent: 校验 Java/目录/EULA → 创建 Job Object → ConPTY 启动进程
  → Agent: 上报状态 Starting → Running、上报输出
  → Server: 推送 instance.status-changed、operation.completed
  → 客户端: 更新状态徽章、终端开始输出
```

### 8.2 终端输入

```text
客户端 SignalR /hubs/v1/terminal → SendInput(instanceId, text)
  → Server: 校验该连接对该实例的授权
  → AgentGateway: 在该实例的终端子流上写入
  → Agent: 按编码与换行模式写入进程标准输入
```

### 8.3 文件下载

```text
客户端 GET /api/v1/instances/{id}/files/content?path=...
  → Server: 授权 → 打开到 Agent 的下载子流 → 边收边写响应体
  → 支持 Range：Server 把范围转发给 Agent，Agent 定位后读取
```

### 8.4 HMCL 客户端更新

```text
HMCL → GET /hmcl/{channel}/server-manifest.json
  → Server: 解析 Channel → 取当前 Release 的物化 manifest → 返回（no-cache + ETag）
HMCL → GET /hmcl/{channel}/overrides/{path} ×N
  → Server: 解析路径 → 查当前 Release 的内容映射 → 从内容库流式返回
```

## 9. 可用性与降级

| 故障 | 行为 |
| --- | --- |
| Server 停止 | Agent 上的实例继续运行；计划任务继续执行；客户端不可用；HMCL 分发不可用 |
| Agent 离线 | 该节点的实例状态转为 `Unreachable`；其他节点不受影响；控制平面保留最后已知状态 |
| Agent 崩溃 | 操作系统回收该节点上的实例进程树；Agent 服务恢复策略自动重启；重启后按自动启动配置恢复实例 |
| 数据库锁竞争 | SQLite 使用 WAL；写操作串行化在单一写入队列；读操作并发 |
| 网络抖动 | Agent 指数退避重连；客户端 SignalR 自动重连并补发事件 |
| 磁盘满 | 写操作前预检；失败时返回明确错误而非损坏数据 |

## 10. 技术选型

| 层 | 技术 |
| --- | --- |
| .NET 运行时 | .NET 10 |
| Desktop | WinUI 3、Windows App SDK、CommunityToolkit.Mvvm |
| 控制平面 | ASP.NET Core、Kestrel |
| Web | React、TypeScript、Fluent UI React v9、Vite |
| 实时事件 | SignalR |
| Server ↔ Agent | gRPC、Protocol Buffers |
| 数据库 | SQLite、EF Core |
| 终端 | ConPTY（Agent 侧）、xterm.js（Web 侧）、Desktop 侧使用受限 WebView2 承载同一终端组件 |
| 编辑器 | Monaco Editor；Desktop 使用受限 WebView2 |
| 安装器 | WiX MSI |
| API 描述 | OpenAPI |
| 测试 | .NET 测试、Playwright、WinUI UI Automation、端到端测试 |

### 10.1 Desktop 中的 WebView2

终端与代码编辑器在 Desktop 中通过受限的 WebView2 承载，复用 Web 侧的 xterm.js 与 Monaco 集成。

约束（安全要求，见 `../design/security-threat_model.md`）：

* WebView2 只加载来自本地打包资源的页面，不加载远程 URL；
* 禁用导航到非本地来源；
* 不启用与宿主的通用互操作，仅暴露一组白名单化的消息通道（终端输入输出、编辑器内容读写、主题变更）；
* 不共享 Cookie 或会话；数据经宿主传入，WebView2 内不直接发起网络请求。

理由：重写一套原生终端渲染与代码编辑器的成本远高于收益，且会导致两端行为不一致（违反 `DEC-05`）。

## 11. 架构测试

`Servitium.Domain.Tests` 与专门的架构测试必须断言：

| 断言 | 目的 |
| --- | --- |
| 项目引用图符合 3.1 | 分层不被破坏 |
| `Domain` 不引用 I/O 与框架类型 | 领域纯净 |
| `Contracts` 不引用其他 Servitium 项目 | 契约独立 |
| 所有 REST 端点经过授权服务 | 无越权入口 |
| 领域模型无 `DateTime` 持久化属性 | 时间一致性 |
| 审计日志实体无 UPDATE/DELETE 路径 | 审计不可修改 |
| `Web` 中不存在手写的 `fetch`/`axios` 调用 | 强制使用生成客户端 |
| `Desktop` 不引用 `Infrastructure` | 客户端纯净 |

## 12. 相关文档

* `../design/architecture-storage.md`
* `../reference/protocol-control_plane.md`
* `../design/security-threat_model.md`
* `../design/architecture-repository.md`
