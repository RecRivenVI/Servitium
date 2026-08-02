# 仓库与解决方案布局

## 1. 目标布局

下列布局按开发阶段逐步创建。阶段 00 只建立治理文件与文档，不创建源码、测试、安装器或构建目录。

```text
Servitium/
├── README.md
├── AGENTS.md
├── Servitium.sln
├── Directory.Build.props
├── Directory.Packages.props
├── NuGet.config
├── .editorconfig
├── .gitattributes
├── .gitignore
├── global.json
│
├── docs/
│
├── src/
│   ├── Servitium.Domain/
│   ├── Servitium.Application/
│   ├── Servitium.Contracts/
│   ├── Servitium.Infrastructure/
│   ├── Servitium.Client/
│   ├── Servitium.Server/
│   ├── Servitium.Agent/
│   ├── Servitium.Desktop/
│   └── Servitium.Web/
│
├── tests/
│   ├── Servitium.Domain.Tests/
│   ├── Servitium.Application.Tests/
│   ├── Servitium.Infrastructure.IntegrationTests/
│   ├── Servitium.Server.IntegrationTests/
│   ├── Servitium.Agent.IntegrationTests/
│   ├── Servitium.Client.ContractTests/
│   ├── Servitium.Desktop.Tests/
│   ├── Servitium.Desktop.UiTests/
│   ├── Servitium.EndToEndTests/
│   └── Fixtures/
│       └── Servitium.JavaServerFixture/
│
├── installer/
│   ├── Servitium.Setup/
│   └── Servitium.Setup.Agent/
│
├── eng/
└── artifacts/
```

## 2. 命名规则

以下规则是强制的，适用于本仓库内的一切现有与未来目录。

| 类别 | 规则 | 示例 |
| --- | --- | --- |
| 仓库级分类目录 | 小写或 `kebab-case` | `src/`、`tests/`、`docs/`、`eng/`、`installer/`、`artifacts/` |
| `.NET` 项目目录 | `PascalCase`，与项目名一致 | `src/Servitium.Server/` |
| `.NET` 项目名 | `Servitium.*` | `Servitium.Infrastructure` |
| C# 源码子目录 | `PascalCase` | `src/Servitium.Agent/Instances/` |
| 测试项目 | 明确后缀 | `.Tests`、`.IntegrationTests`、`.ContractTests`、`.UiTests`、`.EndToEndTests` |
| `docs/` 分类目录 | 小写或 `kebab-case` | `docs/architecture/`、`docs/development/phases/` |
| 普通 Markdown 文件 | 小写 `kebab-case` | `system-architecture.md` |
| 生态固定名 | 保持原样 | `README.md`、`AGENTS.md` |
| Web 内部目录 | Web 生态命名（小写） | `src/`、`features/`、`shared/`、`routes/`、`public/` |
| `wwwroot` 子目录 | 小写 | `wwwroot/css/`、`wwwroot/js/`、`wwwroot/images/` |
| .NET 约定目录 | 保持 .NET 常见大小写 | `Properties/`、`Migrations/`、`Resources/` |
| 工具固定目录 | 保持工具要求 | `.github/`、`.config/`、`.vscode/` |
| 构建与依赖目录 | 不得改名 | `bin/`、`obj/`、`node_modules/`、`artifacts/` |

文档中出现的任何未来目录示例也必须符合上述规则。

## 3. 各项目职责

### `src/Servitium.Domain`

实体、值对象、枚举、状态机、领域规则、领域事件、领域异常。

**不引用**任何其他 Servitium 项目；不引用 EF Core、ASP.NET Core、任何 I/O 或序列化库。

关键类型（示意，最终以实现为准）：`User`、`Node`、`Instance`、`JavaRuntime`、`ScheduledTask`、`HmclChannel`、`HmclRelease`、`InstanceStatus`、`Role`、`WorkingDirectory`（值对象，含 `OwnedByServitium`）。

### `src/Servitium.Application`

用例（命令与查询处理器）、端口接口、编排、授权策略、输入校验、映射到 `Contracts`。

只引用 `Domain` 与 `Contracts`。

端口接口示例：`IInstanceRepository`、`IAgentGateway`、`IClock`、`IPasswordHasher`、`ISecretProtector`、`IFileSystem`、`IContentStore`。

### `src/Servitium.Contracts`

DTO、请求与响应模型、事件信封与事件载荷、稳定错误码常量、界面契约（页面 / 字段 / 动作清单，供双端一致性测试）、文案资源来源文件。

不引用任何其他 Servitium 项目。

### `src/Servitium.Infrastructure`

`Application` 端口的实现：EF Core `DbContext` 与迁移、SQLite 配置、文件系统访问、进程与 ConPTY、Job Object、Windows 注册表访问、DPAPI、Argon2id、HTTP 客户端、gRPC 客户端与服务端封装、内容库。

引用 `Application` 与 `Domain`。不被 `Domain` 或 `Application` 引用。

### `src/Servitium.Client`

.NET API 客户端：REST 调用、SignalR 连接管理、重连、缓存、本机换票。

只引用 `Contracts` 与协议库。**不引用** `Domain`、`Application`、`Infrastructure`。

### `src/Servitium.Server`

ASP.NET Core 组合根。端点、Hub、Agent 网关、HMCL 端点、认证与授权配置、中间件管线、Windows 服务宿主、SPA 托管。

### `src/Servitium.Agent`

Windows 服务组合根。控制平面连接、实例管理、进程、文件、配置、模组、Java、本地调度、指标。

### `src/Servitium.Desktop`

WinUI 3 组合根。视图、视图模型、导航、托盘、通知、本机会话。

只引用 `Client` 与 `Contracts`。**不引用** `Infrastructure`。

### `src/Servitium.Web`

React 应用。构建产物输出到 `Servitium.Server` 的 `wwwroot/`。

```text
src/Servitium.Web/
├── package.json
├── vite.config.ts
├── tsconfig.json
├── index.html
├── public/
└── src/
    ├── app/
    ├── features/
    │   ├── auth/
    │   ├── dashboard/
    │   ├── nodes/
    │   ├── instances/
    │   ├── terminal/
    │   ├── files/
    │   ├── configurations/
    │   ├── mods/
    │   ├── schedules/
    │   ├── users/
    │   ├── settings/
    │   ├── audit/
    │   └── hmcl/
    ├── shared/
    ├── api/
    ├── realtime/
    └── i18n/
```

## 4. 测试项目职责

| 项目 | 范围 | 依赖真实资源 |
| --- | --- | --- |
| `Servitium.Domain.Tests` | 领域规则、状态机、值对象、架构约束 | 无 |
| `Servitium.Application.Tests` | 用例编排、授权策略、校验（端口用测试替身） | 无 |
| `Servitium.Infrastructure.IntegrationTests` | EF Core 与迁移、真实 SQLite、真实文件系统、ConPTY、Job Object、配置解析器无损往返 | 文件系统、进程 |
| `Servitium.Server.IntegrationTests` | REST、SignalR、认证授权、限流、错误模型、HMCL 端点、Agent 网关（用 Agent 桩） | SQLite、内存主机 |
| `Servitium.Agent.IntegrationTests` | Agent 指令处理、实例生命周期、文件、计划任务、本地自治（用控制平面桩） | 文件系统、进程 |
| `Servitium.Client.ContractTests` | OpenAPI 与生成客户端的一致性、协议兼容性回归 | 无 |
| `Servitium.Desktop.Tests` | 视图模型逻辑 | 无 |
| `Servitium.Desktop.UiTests` | WinUI UI Automation | 真实 Desktop 进程 |
| `Servitium.EndToEndTests` | Playwright（Web）+ 真实 Server + 真实 Agent + 真实 Minecraft 测试服务端 + 真实 HMCL 客户端 | 全部 |
| `Fixtures/Servitium.JavaServerFixture` | 可控的 Java 测试服务端：可配置启动耗时、输出、是否响应停止命令、是否派生子进程、是否崩溃、退出码 | Java 运行时 |

### 4.1 `Servitium.JavaServerFixture`

一个最小的 Java 程序（打包为可执行 JAR），用于替代真实 Minecraft 服务端进行绝大多数测试。必须支持通过参数或环境变量控制：

| 行为 | 用途 |
| --- | --- |
| 启动耗时 | 验证 `Starting` 状态与超时 |
| 周期性输出（含 ANSI 与非 ASCII） | 验证终端、编码、缓冲 |
| 是否响应 `stop` | 验证停止序列的降级 |
| 是否派生子进程 | 验证 Job Object 进程树回收 |
| 是否立即崩溃、退出码 | 验证崩溃识别与自动重启 |
| 是否监听端口并响应 Server List Ping | 验证玩家查询 |
| 是否提供 RCON 端点 | 验证 RCON |
| 是否创建 `eula.txt` | 验证 EULA 流程 |

真实 Minecraft 服务端仅在端到端与用户验收阶段使用。

## 5. 构建配置文件

| 文件 | 内容 |
| --- | --- |
| `global.json` | 固定 .NET SDK 版本与 `rollForward` 策略 |
| `Directory.Build.props` | 统一 `TargetFramework`、`LangVersion`、`Nullable`、`TreatWarningsAsErrors`、分析器级别、确定性构建、版本号来源 |
| `Directory.Packages.props` | 中央包版本管理（`ManagePackageVersionsCentrally`） |
| `NuGet.config` | 包源；启用包源映射；启用锁定文件模式 |
| `.editorconfig` | C# 与 TypeScript 的格式与命名规则；作为 `dotnet format` 的依据 |
| `.gitattributes` | 行尾规范化；二进制文件标记 |
| `.gitignore` | 忽略 `bin/`、`obj/`、`artifacts/`、`node_modules/`、本地设置、测试产物 |

## 6. `eng/` 目录

工程脚本目录，在后端工程基础阶段创建。

```text
eng/
├── Validate.ps1          完整验证入口（见 build-and-validation.md）
├── Build.ps1
├── Test.ps1
├── Generate.ps1          OpenAPI 客户端、protobuf、文案资源生成
├── Clean.ps1
└── modules/              PowerShell 公共函数
```

## 7. `artifacts/` 目录

全部构建产物的统一输出根，不纳入版本控制。

```text
artifacts/
├── bin/<项目>/<配置>/
├── obj/<项目>/<配置>/
├── packages/
├── installers/
├── web/
├── openapi/
└── test-results/
```

`Directory.Build.props` 中通过 `ArtifactsPath` 统一重定向。

## 8. `installer/` 目录

```text
installer/
├── Servitium.Setup/          完整安装包（Server + Agent + Desktop + Web）
└── Servitium.Setup.Agent/    远程 Agent 专用安装包
```

WiX 项目。安装器实现属于交付阶段；阶段 00 只定义位置与职责。

## 9. 解决方案组织

`Servitium.sln` 中的解决方案文件夹：

```text
src
tests
installer
eng（含 Directory.*.props、.editorconfig 等的链接）
docs（README、AGENTS 的链接）
```

## 10. 分支与提交

* `main`：稳定分支；
* `develop`：当前开发分支，不继承旧产品代码；
* `legacy`：旧产品历史，只读，不合并到 `develop`。

提交规则见 `AGENTS.md`。默认不得提交，需用户明确授权。

## 11. 目录创建时机

| 目录 | 创建阶段 |
| --- | --- |
| `docs/` | 阶段 00 |
| `README.md`、`AGENTS.md` | 阶段 00 |
| `src/`、`tests/`、`eng/`、`artifacts/`、构建配置文件 | 阶段 10 |
| `src/Servitium.Domain` … `Servitium.Contracts` | 阶段 11 |
| `src/Servitium.Server`、`Servitium.Infrastructure` | 阶段 12 |
| `src/Servitium.Agent` | 阶段 13 |
| `src/Servitium.Web` | 阶段 21 |
| `src/Servitium.Desktop`、`src/Servitium.Client` | 阶段 20 与 22 |
| `installer/` | 交付阶段 |
| `.github/workflows/` | 仅在用户于交付阶段明确选择后创建 |

**阶段 00 不得创建上表中除 `docs/`、`README.md`、`AGENTS.md` 之外的任何条目，也不得创建空目录或 `.gitkeep`。**
