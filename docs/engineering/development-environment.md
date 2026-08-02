# 开发环境

本文件是 Servitium 开发、调试与本地运行的统一环境基线。

## 1. 必需组件

| 组件 | 版本 | 用途 |
| --- | --- | --- |
| Windows | 11 x64 或 Windows Server 2022/2025 x64 | 目标平台；ConPTY 与 Job Object 需要 |
| .NET SDK | 10.x（具体版本固定在 `global.json`） | 全部 .NET 项目 |
| Windows App SDK | 与 WinUI 3 匹配的稳定版 | Desktop |
| Node.js | 22 LTS 或更高 LTS | Web |
| WebView2 Runtime | Evergreen | Desktop 中的终端与编辑器 |
| Java | 任意受支持的 JDK（用于测试夹具与 Java 探测测试） | 测试 |
| PowerShell | 7+ | `eng/` 脚本 |
| Git | 2.40+ | 版本控制 |

可选：

| 组件 | 用途 |
| --- | --- |
| WiX Toolset | 交付阶段的 MSI 构建 |
| Playwright 浏览器 | 端到端测试（由 `playwright install` 获取） |
| 真实 Minecraft Java 服务端 | 端到端与用户验收 |
| HMCL 客户端 | HMCL 端到端验证 |

## 2. 环境校验

阶段 10 创建 `eng/CheckEnvironment.ps1`，校验上表中的必需组件并输出缺失项与获取方式。该脚本在 `eng/Validate.ps1` 起始处调用。

## 3. 首次搭建

```powershell
# 1. 还原 .NET 依赖（锁定模式）
dotnet restore --locked-mode

# 2. 安装 Web 依赖
npm ci --prefix src/Servitium.Web

# 3. 生成代码（OpenAPI 客户端、protobuf、文案资源）
./eng/Generate.ps1

# 4. 构建
./eng/Build.ps1 -Configuration Debug
```

## 4. 开发运行

### 4.1 端口与地址

| 组件 | 开发端口 | 说明 |
| --- | --- | --- |
| `Servitium.Server` | `https://127.0.0.1:5344` | 开发证书 |
| Vite 开发服务器 | `http://127.0.0.1:5173` | 代理 `/api`、`/hubs`、`/hmcl` 到 Server |
| `Servitium.Agent` | 无监听端口 | 主动连接 Server |
| HMCL 测试静态服务 | `http://127.0.0.1:5399` | 端到端测试用 |

开发环境固定仅监听回环。

### 4.2 数据目录

开发时**不使用** `%ProgramData%`，改用仓库外的独立目录，避免污染正式数据：

| 组件 | 开发数据目录 |
| --- | --- |
| Server | `%LocalAppData%\Servitium.Dev\Server` |
| Agent | `%LocalAppData%\Servitium.Dev\Agent` |
| Desktop | `%LocalAppData%\Servitium.Dev\Desktop` |

通过环境变量 `SERVITIUM_DATA_ROOT` 覆盖。测试运行时使用一次性临时目录。

### 4.3 启动方式

以控制台应用方式运行（而非 Windows 服务），便于调试：

```powershell
# 终端 1：控制平面
dotnet run --project src/Servitium.Server -- --environment Development

# 终端 2：节点（首次需注册令牌）
dotnet run --project src/Servitium.Agent -- --environment Development

# 终端 3：Web 开发服务器
npm run dev --prefix src/Servitium.Web

# 终端 4（可选）：桌面端
dotnet run --project src/Servitium.Desktop
```

阶段 10 提供 `eng/Run.ps1` 一次性拉起 Server + Agent + Web，并在退出时清理子进程。

### 4.4 首次注册 Agent

1. 启动 Server，浏览器访问 `https://127.0.0.1:5344` 完成初始化；
2. 在节点页生成注册令牌；
3. 把令牌写入 Agent 的开发配置或通过命令行参数传入：
   `dotnet run --project src/Servitium.Agent -- --register-token <令牌> --server https://127.0.0.1:5344`；
4. Agent 连接成功后节点页显示为在线。

### 4.5 开发证书

```powershell
dotnet dev-certs https --trust
```

Agent 在开发环境中默认信任本机开发证书。

## 5. 调试

### 5.1 断点调试

| 组件 | 方式 |
| --- | --- |
| Server / Agent | 直接附加或以调试启动 |
| Desktop | 以调试启动；WebView2 内容通过 Edge DevTools 调试（仅 Debug 构建启用） |
| Web | 浏览器 DevTools + source map |

### 5.2 日志

开发环境默认日志级别 `Debug`，输出到控制台与文件。可通过环境变量调整：

```powershell
$env:SERVITIUM_Logging__LogLevel__Default = "Trace"
```

日志文件位置：`<开发数据目录>\logs\`。

### 5.3 数据库检查

开发数据库为 SQLite 文件，可用任意 SQLite 工具打开。**不要在服务运行时写入**（WAL 模式下读取是安全的）。

### 5.4 进程识别

Server 与 Agent 在启动时把自身 PID 写入 `<开发数据目录>\pid`，并在控制台首行输出：

```text
Servitium.Server  PID=12345  Env=Development  Data=<路径>  Listen=https://127.0.0.1:5344
```

用户测试时据此识别与终止进程（见 `docs/testing/user-testing-procedure.md`）。

## 6. 测试环境

### 6.1 隔离原则

测试**必须**使用：

* 独立的临时数据目录（每次运行新建，结束时清理）；
* 独立的 SQLite 文件（不复用开发库）；
* 独立的临时工作目录（不指向真实实例）；
* 随机端口（避免与开发实例冲突）。

违反隔离的测试视为缺陷。

### 6.2 测试夹具

`tests/Fixtures/Servitium.JavaServerFixture` 提供可控的 Java 测试服务端，行为由参数控制（见 `docs/architecture/repository-and-solution-layout.md` 第 4.1 节）。构建时产出可执行 JAR 到 `artifacts/test-fixtures/`。

需要 Java 的测试在缺少 Java 时**阻塞并报告**，不得静默跳过。

### 6.3 端到端环境

```text
真实 Server（Testing 环境，临时数据目录）
  + 真实 Agent（临时数据目录）
  + JavaServerFixture 或真实 Minecraft 服务端
  + Playwright 浏览器
  + （HMCL 场景）真实 HMCL 客户端
```

端到端测试由 `eng/Validate.ps1` 的对应阶段拉起与销毁全部进程。

## 7. 常用命令

```powershell
# 完整验证（与验收使用同一入口）
./eng/Validate.ps1

# 仅构建
./eng/Build.ps1 -Configuration Release

# 仅测试（可按分类）
./eng/Test.ps1 -Category Unit
./eng/Test.ps1 -Category Integration
./eng/Test.ps1 -Category Security

# 重新生成代码并校验一致性
./eng/Generate.ps1 -Verify

# 格式化
dotnet format
npm run format --prefix src/Servitium.Web

# 清理
./eng/Clean.ps1
```

## 8. 需要提升权限的操作

下列操作需要管理员权限。开发与测试阶段若当前会话无权限，按 `docs/development/development-workflow.md` 第 7 节的**权限阻塞流程**处理：停止相关阶段并请求用户协助，**不得跳过、不得伪造通过、不得用静态审查代替**。

| 操作 | 用途 |
| --- | --- |
| 安装/卸载 Windows 服务 | 服务宿主验收 |
| 配置服务恢复策略 | 恢复策略验收 |
| 设置目录 ACL | 数据目录安全验收 |
| 写入 `%ProgramData%\Servitium` | 正式路径验收 |
| 创建防火墙规则 | 非回环暴露验收 |
| 安装 MSI | 安装器验收 |
| 在干净系统执行安装测试 | 交付阶段 |
| 读取 `HKLM` 下的部分注册表项 | 通常不需要提升；若受限则同上处理 |

## 9. 编辑器配置

| 项 | 说明 |
| --- | --- |
| `.editorconfig` | 统一缩进、行尾、命名与分析器规则 |
| 推荐扩展（VS Code） | C# Dev Kit、ESLint、Prettier、EditorConfig |
| 行尾 | 仓库内统一 LF，由 `.gitattributes` 规范化；`.ps1`、`.cmd`、`.bat` 使用 CRLF |
| 编码 | UTF-8；C# 文件带 BOM（便于 MSBuild 与部分工具正确识别），其余不带 |

## 10. 网络依赖

开发过程需要访问：

| 目标 | 用途 |
| --- | --- |
| NuGet 包源 | .NET 依赖 |
| npm registry | Web 依赖 |
| Playwright 浏览器下载 | 端到端测试 |

若环境无法访问外部包源，必须在开始编码前向用户报告并请求协助（离线包缓存或代理配置），**不得以跳过依赖或降低依赖版本的方式绕过**。

产品运行时**不依赖**任何外部服务（`DEC-02`）。

## 11. 已知环境问题的处置

| 问题 | 处置 |
| --- | --- |
| 缺少 Windows App SDK | 阻塞 Desktop 阶段，报告并请求安装 |
| 缺少 WebView2 Runtime | 阻塞 Desktop 终端与编辑器测试，报告并请求安装 |
| 缺少 Java | 阻塞需要 Java 的测试，报告并请求安装 |
| 端口被占用 | 使用随机端口重试；开发端口冲突时报告 |
| 杀毒软件锁定构建产物 | 报告并请求把仓库与 `artifacts/` 加入排除项 |
| 长路径限制 | 报告并请求启用 Windows 长路径支持 |
