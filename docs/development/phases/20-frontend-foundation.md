# 阶段 20：前端共享基础

## 1. 阶段目的

建立 Web 与 Desktop 共用的基础：.NET API 客户端、生成的 TypeScript 客户端、界面契约与一致性测试框架、文案资源生成、设计令牌映射、实时连接管理、两端的项目骨架。

## 2. 需求编号

`REQ-I18N-001` … `REQ-I18N-004`、`REQ-DSK-050`（一致性基础）、`REQ-PLT-030`（客户端侧）、`REQ-USR-050`（语言切换基础）。

## 3. 前置条件

* **阶段 18 通过**（硬门禁）；
* `docs/records/current-status.md` 明确记录后端验收通过。

## 4. 范围

* `Servitium.Client`（.NET API 客户端）；
* `Servitium.Web` 项目骨架与工具链；
* `Servitium.Desktop` 项目骨架；
* 生成 TypeScript API 客户端；
* 文案资源生成（TS 模块与 `.resw`）；
* 界面契约文件的消费与一致性测试框架；
* 设计令牌到两端的映射；
* 实时连接管理（两端）；
* 共享的错误码到文案的映射；
* 两端的应用外壳（导航、主题、错误边界），不含业务页面。

## 5. 非目标

* 不实现任何业务页面（阶段 21、22）；
* 不实现托盘与通知（阶段 22）；
* 不实现终端与编辑器的集成（阶段 21）。

## 6. 需要创建的项目或目录

```text
src/Servitium.Client/
├── Rest/          REST 调用封装
├── Realtime/      SignalR 连接与订阅
├── Session/       会话管理、本机换票、续期
└── Errors/        错误码到异常的映射

src/Servitium.Web/
├── package.json、vite.config.ts、tsconfig.json、index.html、public/
└── src/{app,features,shared,api,realtime,i18n}/

src/Servitium.Desktop/
├── App/、Shell/、Views/、ViewModels/、Services/、Resources/
└── Package.appxmanifest 等 WinUI 3 工程文件

tests/Servitium.Desktop.Tests/
tests/Servitium.Desktop.UiTests/
tests/Servitium.EndToEndTests/
```

## 7. 需要实现的类型或模块

### 7.1 `Servitium.Client`

| 组件 | 职责 |
| --- | --- |
| `ServitiumApiClient` | 覆盖全部 REST 资源的强类型方法（DTO 来自 `Contracts`） |
| `SessionManager` | 登录、本机换票、会话续期、登出、CSRF 令牌管理 |
| `EventsConnection` | `/hubs/v1/events` 的连接、订阅、重连、`Resume` |
| `TerminalConnection` | `/hubs/v1/terminal` 的连接与方法 |
| `ProblemDetailsMapper` | Problem Details → 带 `code` 的异常 |
| `RetryPolicy` | 网络错误与 5xx 重试；4xx 不重试 |

**不引用** `Domain`、`Application`、`Infrastructure`（架构测试守护）。

### 7.2 TypeScript API 客户端

由 `eng/Generate.ps1` 从 `artifacts/openapi/swagger.json` 生成到 `src/Servitium.Web/src/api/`。生成产物不手工编辑；`Generate.ps1 -Verify` 在验证入口中断言一致。

### 7.3 文案资源生成

从 `src/Servitium.Contracts/Localization/{zh-Hans,en-US}.json` 生成：

* Web：`src/Servitium.Web/src/i18n/generated/`（类型安全的键与译文）；
* Desktop：`src/Servitium.Desktop/Resources/{zh-Hans,en-US}/Resources.resw`。

生成时校验：键集合一致、无空值、占位符一致。

### 7.4 界面契约与一致性测试

`src/Servitium.Contracts/Ui/` 中的契约文件描述：页面编号、路由、权限、区块顺序、字段键、动作键、状态键、文案键、是否平台专属。

一致性测试框架：

* Web 侧：在端到端测试中遍历页面，采集实际渲染的区块、字段与动作的 `data-servitium-*` 标记；
* Desktop 侧：在 UI 自动化测试中遍历页面，采集 `AutomationId`；
* 比对采集结果与契约；差异即失败（平台专属条目除外）。

本阶段建立框架并让契约中已定义的「应用外壳」部分通过；业务页面在阶段 21、22 逐步纳入。

### 7.5 设计令牌映射

* Web：把 `shared-ui-specification.md` 第 1 节的语义令牌映射到 Fluent UI React v9 的主题令牌；
* Desktop：映射到 WinUI 3 的主题资源；
* 两端的映射表从同一份令牌定义文件生成，保证一致。

### 7.6 应用外壳

| 端 | 内容 |
| --- | --- |
| Web | 路由表、主题提供者、错误边界、全局提示、连接状态提示条、加载与空态组件、确认对话框组件、页头与卡片组件 |
| Desktop | `NavigationView` 外壳、主题与 Mica、返回栈、错误呈现、全局提示 |

两端的导航项从同一份契约生成。

### 7.7 实时连接管理

| 端 | 内容 |
| --- | --- |
| Web | 单例连接、按页面订阅与退订、自动重连与 `Resume`、断线提示 |
| Desktop | 同上，由 `Servitium.Client` 提供 |

## 8. 数据模型

无服务端模型变更。前端的本地状态模型在此定义（查询键规范、缓存策略）。

## 9. API 或事件

不新增。消费阶段 12–17 已实现的全部接口。

## 10. 实施顺序

1. `Servitium.Client` 的 REST 封装（从 `Contracts` 的 DTO）；
2. `Servitium.Client` 的会话管理与 CSRF；
3. `Servitium.Client` 的实时连接；
4. `Servitium.Client` 的错误映射与重试；
5. `Servitium.Client.ContractTests` 补充：客户端方法覆盖全部端点；
6. `eng/Generate.ps1` 扩展：TypeScript 客户端生成；
7. `eng/Generate.ps1` 扩展：文案资源生成（两端）；
8. 创建 `Servitium.Web` 项目与工具链（Vite、TS、ESLint、Prettier）；
9. Web 的设计令牌映射与主题；
10. Web 的应用外壳（路由、导航、错误边界、全局组件）；
11. Web 的实时连接管理；
12. Web 的 i18n 接入与语言切换；
13. 创建 `Servitium.Desktop` 项目（WinUI 3）；
14. Desktop 的设计令牌映射与主题、Mica；
15. Desktop 的 `NavigationView` 外壳与返回栈；
16. Desktop 的 i18n 接入；
17. 界面契约的消费与一致性测试框架（两端）；
18. 创建 `Servitium.EndToEndTests` 骨架（Playwright 安装与基础夹具）；
19. 扩展 `eng/Validate.ps1`：Web lint、TypeScript 检查、包体积、UI 测试、端到端测试阶段；
20. 测试；
21. 运行 `eng/Validate.ps1`。

## 11. 关键算法

| 算法 | 说明 |
| --- | --- |
| 查询键规范 | `[资源, 稳定序列化的参数对象]`；参数对象按键名排序后序列化 |
| 缓存失效 | 变更操作成功后按资源失效；实时事件按作用域失效 |
| 重连与 `Resume` | 重连成功后携带 `lastEventId` 请求补发；超出缓冲范围时触发全量刷新 |
| 错误码到文案 | `error.<code>` 键查找；未命中时回退 `error.unknown` 并展示 `traceId` |
| 一致性比对 | 采集实际清单 → 与契约按页面逐项比对 → 输出缺失、多余、顺序不符三类差异 |

## 12. Windows 平台事项

* Desktop 需要 Windows App SDK 与 WebView2 Runtime；缺失时按环境阻塞流程处理；
* Desktop 的 UI 自动化测试需要交互式桌面会话；
* Web 的 Playwright 浏览器需要下载。

## 13. 安全要求

* `Servitium.Client` 不存储明文凭据；Desktop 的会话令牌存 Windows 凭据管理器；
* Web 不接触会话令牌（HttpOnly Cookie）；
* CSP 在本阶段生效并验证（Web 外壳必须在 CSP 下正常工作，`SEC-120`）；
* 两端不得硬编码任何用户可见字符串。

## 14. 自动化测试

| 编号族 | 内容 |
| --- | --- |
| `CTR-100` | `Servitium.Client` 的方法覆盖 OpenAPI 中的全部端点 |
| `CTR-110` | 生成的 TypeScript 客户端与 `swagger.json` 一致 |
| `CTR-120` | 文案资源两种语言键集合一致、占位符一致、无空值 |
| `CTR-130` | 生成产物与来源一致（`Generate.ps1 -Verify`） |
| `ARC-050` | `Servitium.Client` 不引用 `Domain`/`Application`/`Infrastructure` |
| `ARC-051` | `Servitium.Desktop` 不引用 `Infrastructure` |
| `ARC-052` | Web 中不存在手写的 `fetch`/`axios` 调用 |
| `ARC-053` | Web 中不存在硬编码的用户可见字符串（按 lint 规则） |

## 15. 集成测试

| 编号 | 内容 |
| --- | --- |
| `INT-1500` | `Servitium.Client` 对真实 Server 完成登录、查询、订阅事件、断线重连 |
| `INT-1510` | Web 外壳在真实 Server 上启动、登录、导航、语言切换 |
| `INT-1520` | Desktop 外壳在真实 Server 上启动、登录、导航、语言切换 |
| `INT-1530` | 一致性测试框架对「应用外壳」部分通过 |
| `INT-1540` | CSP 下 Web 外壳无控制台错误 |

## 16. 人工测试

无。

## 17. 调试启动方式

```powershell
dotnet run --project src/Servitium.Server -- --environment Development
dotnet run --project src/Servitium.Agent -- --environment Development
npm run dev --prefix src/Servitium.Web
dotnet run --project src/Servitium.Desktop
```

## 18. 验收责任方

编码 Agent 自主验收。

## 19. 完成条件

1. `Servitium.Client` 覆盖全部端点并通过契约测试；
2. TypeScript 客户端可生成且一致性校验通过；
3. 文案资源可生成且两种语言一致；
4. Web 与 Desktop 的应用外壳可运行、可登录、可导航、可切换语言与主题；
5. 一致性测试框架建立并对外壳部分通过；
6. `eng/Validate.ps1` 的 Web lint、TypeScript 检查、包体积、UI 测试、端到端测试阶段已接入；
7. `CTR-100` … `CTR-130`、`ARC-050` … `ARC-053` 通过；
8. `INT-1500` … `INT-1540` 通过；
9. `eng/Validate.ps1` 全量通过。

## 20. 禁止提前进入下一阶段的条件

* 阶段 18 未通过（硬门禁）；
* TypeScript 客户端为手写而非生成；
* 文案资源两端不同源；
* 一致性测试框架未建立；
* Web 外壳在 CSP 下有错误。

## 21. 需要更新的动态记录

* `docs/records/current-status.md`
* `docs/records/development-log.md`
* `docs/records/decision-log.md`（记录查询层、图表库、Playwright 配置等实现级选择）
* `docs/records/validation-log.md`
* `docs/testing/requirements-traceability.md`
* `docs/testing/acceptance-matrix.md`
