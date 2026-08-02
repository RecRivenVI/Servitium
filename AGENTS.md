# AGENTS.md

本文件约束在 Servitium 仓库中工作的全部编码 Agent。

违反本文件的行为视为缺陷。

## 1. 规则优先级

冲突时按下列顺序裁决，序号小的优先：

1. 用户在当前会话中的明确指示；
2. 本文件；
3. `docs/product/decision-baseline.md`（18 条固定决策 + 13 条派生约束）；
4. `docs/product/functional-specification.md`（需求基线）；
5. `docs/architecture/**`、`docs/design/**`、`docs/reference/**`；
6. `docs/engineering/**`、`docs/development/**`、`docs/testing/**`；
7. `docs/records/**`（事实记录，不构成规则）；
8. 代码中的既有实现。

发现 2–6 之间存在矛盾时，**不要自行选择一方**：写入 `docs/records/change-requests.md` 并等待用户裁决。

## 2. 产品边界

Servitium 是面向 Windows 的 Minecraft Java 服务端托管与整合内容分发平台。

**只做**：Minecraft Java 服务端。

**不做**：通用进程托管、Steam / SteamCMD、Minecraft 基岩版、MCDReforged、Docker 与镜像管理、全局 WebShell、模板与快速安装市场、商业兑换与商城、内置内网穿透（如 OpenFRP）、第三方模组市场检索与下载、旧产品数据迁移、旧架构兼容层、Portable 运行模式、其他启动器的整合包格式。

不得以「顺手加上」「未来可能需要」为由引入排除项。

## 3. 必读文档与顺序

首次接手时按此顺序读完：

```text
1  AGENTS.md（本文件）
2  docs/index.md
3  docs/product/product-definition.md
4  docs/product/decision-baseline.md
5  docs/product/functional-specification.md
6  docs/architecture/system-architecture.md
7  docs/architecture/protocols-and-contracts.md
8  docs/architecture/security-model.md
9  docs/architecture/data-and-storage.md
10 docs/architecture/repository-and-solution-layout.md
11 docs/engineering/engineering-standards.md
12 docs/engineering/development-environment.md
13 docs/engineering/build-and-validation.md
14 docs/development/development-workflow.md
15 docs/development/master-plan.md
16 docs/development/phases/<当前阶段>.md
17 docs/records/current-status.md
```

按需查阅：`docs/design/**`（界面）、`docs/reference/**`（参考证据）、`docs/testing/**`（测试）、`docs/product/hmcl-distribution-specification.md`（HMCL）。

## 4. 固定决策

以下由用户明确确定，**不得重新征询、不得自行改变**：

| 主题 | 决策 |
| --- | --- |
| 节点范围 | Windows 分布式：中央控制平面 + WinUI/Web 客户端 + 多个 Windows Agent |
| 联网边界 | 剔除厂商与商业在线依赖；保留自托管 Web、节点通信、HMCL 分发、URL 下载、私有源、可自托管 OIDC |
| Web 技术 | React、TypeScript、Fluent UI React v9，由 ASP.NET Core 托管构建产物 |
| 运行形态 | Server 与 Agent 以 Windows 服务常驻 |
| 双端一致 | Desktop 与 Web 的信息架构、字段、动作、状态、权限、文案一致；仅允许平台相关的响应式重排 |
| 分支起点 | `develop` 是不继承旧产品代码的全新起点 |
| 交付形态 | 完整安装包 + 远程 Agent 专用安装包；不提供 Portable |
| 实例范围 | 仅 Minecraft Java 服务端 |
| 工作目录 | 可登记任意绝对路径；路径是实例属性，不是实例身份 |
| HMCL 范围 | 完整的 Archive、校验、不可变 Release、原子 Channel、客户端自动更新流水线 |
| Desktop 登录 | Windows 身份 + 受 ACL 保护的本机通道换取短期会话 |
| 实例删除 | 删除登记与删除目录分离；**外部工作目录永不递归删除** |
| 系统支持 | Desktop：Windows 11 x64；Server 与 Agent：Windows 11、Windows Server 2022/2025 x64 |
| Java 启动 | 默认结构化向导；另有受限制的高级原始命令模式 |
| 容量目标 | 8 个 Agent、64 个实例、20 个并发登录会话、8 个并发终端 |
| Web 暴露 | 默认仅监听回环；非回环开放必须配置受信 TLS 或明确的可信反向代理模式 |
| 首发语言 | `zh-Hans` 与 `en-US` |
| 界面设计 | Fluent 2；MCSManager 只提供功能、信息架构与交互行为参考 |

派生约束见 `docs/product/decision-baseline.md` 第 2 节。

## 5. 参考路径实地考察与净室原则

`docs/reference/**` 保存的是带版本定位和验证级别的既有证据，可作为实现起点，但不代表参考实现此后不会变化，也不替代开发过程中的针对性核实。

编码 Agent 在实现、测试或排查兼容性问题时，应按实际需要前往下列绝对路径实地考察：

```text
D:\GitHubPublic\HMCL
D:\GitHubPublic\MCSManager
D:\GitHubRecRivenVI\PrismLauncher
E:\Minecraft\MinecraftServersDuoyun\test
```

各路径的主要用途：

* HMCL：协议、更新算法、HTTP 行为与客户端可观察结果；
* MCSManager：功能分布、页面布局、交互流程、权限与多节点行为；
* Prism Launcher：原生实例 ZIP 的导出路径、目录结构、`instance.cfg`、`mmc-pack.json` 与游戏目录内容；
* 本地整合包发布原型：Prism ZIP 转换、候选内容、不可变 Release、稳定 Channel、显式发布与双格式派生。

实地考察应先提出与当前实现直接相关的问题。Git 仓库记录分支、提交和工作树状态；非 Git 参考记录文件时间、证据清单哈希、证据路径和验证方式。静态证据不足时，可在参考路径外的隔离环境中运行有边界的验证。

四个参考路径必须保持只读。编译、依赖安装、探针和运行产物一律放在参考路径外的临时目录或隔离副本。`E:\Minecraft\MinecraftServersDuoyun\test` 中的真实 ZIP、Release、日志、运行状态和备份属于用户数据；没有针对具体验证的授权时，不得读取、复制、移动、解压、执行或用作测试样本。

实地考察用于补充和校正事实证据，不自动改变 Servitium 的产品范围、协议契约、界面契约、阶段顺序或安全要求。新证据只是确认既有决定时，记入 `docs/records/development-log.md` 或 `docs/records/validation-log.md`；新证据与受保护文档冲突，或据此需要改变任何既有决定时，写入 `docs/records/change-requests.md`，附证据来源与复现方式，等待用户裁决。

净室原则始终有效：可以观察外部行为、记录事实和定位标识符，并据此独立设计；不得复制、移植或改写后复用参考项目的源码、资源、样式、图标、界面文案、品牌元素或其他受保护表达，避免引入许可、协议或知识产权纠纷。

## 6. 后端优先与阶段门禁

阶段顺序固定（见 `docs/development/master-plan.md`）：

```text
00 → 10 → 11 → 12 → 13 → 14 → 15 → 16 → 17 → 18(硬门禁)
→ 20 → 21 → 22 → 23 → 24(硬门禁) → 等待用户明确启动交付
```

规则：

| 规则 | 说明 |
| --- | --- |
| 严格串行 | 阶段之间不并行；阶段内按「实施顺序」串行 |
| 后端优先 | **阶段 18 未通过前，不得开始正式前端开发** |
| 门禁不可弱化 | 不得以变更请求的方式绕过阶段 18 与阶段 24 |
| 不提前 | 阶段文档的「禁止进入下一阶段的条件」任一成立即停止 |
| 不重排 | 阶段过大时提出拆分建议，不得自行重排或合并 |
| 缺陷即修 | 发现前一阶段的缺陷立即修复并补测试，不推迟 |

阶段 18 的例外：为验证后端而写的最小化脚本或临时页面不构成正式前端开发，且不得保留在仓库中。

## 7. 项目依赖方向

```text
Domain      ← Application ← Infrastructure ← Server
                  ↑                          ← Agent
Contracts ────────┘  ← Client ← Desktop
                     ← Web（经生成的 TS 客户端）
```

| 规则 | 强制手段 |
| --- | --- |
| `Domain` 不引用任何其他 Servitium 项目，不引用 EF Core、ASP.NET Core 或 I/O 库 | 架构测试 |
| `Application` 只引用 `Domain` 与 `Contracts` | 架构测试 |
| `Contracts` 独立 | 架构测试 |
| `Infrastructure` 实现 `Application` 的端口，不被 `Domain`/`Application` 引用 | 架构测试 |
| `Server` 与 `Agent` 是组合根 | 约定 |
| `Client` 只依赖协议与 `Contracts` | 架构测试 |
| `Desktop` 只经 `Client` 访问 Server，不引用 `Infrastructure` | 架构测试 |
| `Web` 只使用生成的 TypeScript 客户端，不手写 `fetch`/`axios` | lint + 架构测试 |

## 8. 命名规则

| 类别 | 规则 |
| --- | --- |
| 仓库级分类目录 | 小写或 `kebab-case`：`src/`、`tests/`、`docs/`、`eng/`、`installer/`、`artifacts/` |
| `.NET` 项目目录与项目名 | `PascalCase`，`Servitium.*` |
| C# 源码子目录 | `PascalCase` |
| 测试项目后缀 | `.Tests`、`.IntegrationTests`、`.ContractTests`、`.UiTests`、`.EndToEndTests` |
| `docs/` 分类目录 | 小写或 `kebab-case` |
| 普通 Markdown 文件 | 小写 `kebab-case` |
| 生态固定名 | `README.md`、`AGENTS.md` 保持原样 |
| Web 内部 | `src/`、`features/`、`shared/`、`routes/`、`public/` 用 Web 生态命名 |
| `wwwroot` 子目录 | 小写：`css/`、`js/`、`images/` |
| .NET 约定目录 | 保持常见大小写：`Properties/`、`Migrations/`、`Resources/` |
| 工具固定目录 | `.github/`、`.config/`、`.vscode/` |
| 构建与依赖目录 | `bin/`、`obj/`、`node_modules/`、`artifacts/` 不得改名 |

## 9. 编码规则

完整规范见 `docs/engineering/engineering-standards.md`。要点：

| 规则 | 说明 |
| --- | --- |
| 可空引用类型 | 全项目启用 |
| 警告即错误 | 启用；不得无理由 `#pragma warning disable` |
| 时间 | 经 `IClock`；`DateTimeOffset` + UTC；`Domain` 中无 `DateTime` 持久化属性 |
| 路径 | 经路径校验服务；不直接拼接字符串路径 |
| 进程 | 经 `IProcessLauncher`；**参数数组，绝不经 shell** |
| 取消 | 异步方法接受并传递 `CancellationToken` |
| 异步 | 不使用 `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` |
| 日志 | 结构化、命名占位符、不记录秘密、服务日志固定 `en-US` |
| 文案 | 界面不得硬编码用户可见字符串；服务端返回稳定错误码，客户端本地化 |
| 注释 | 中文；解释「为什么」 |
| TODO | 禁止未登记的 TODO；确需时写 `// TODO(PB-###): ...` 并在 `problem-log.md` 有条目 |
| 生成代码 | 不手工编辑；`eng/Generate.ps1 -Verify` 必须无差异 |

## 10. 测试规则

| 规则 | 说明 |
| --- | --- |
| 实际运行 | 验收必须实际运行，**不得以代码审查代替** |
| 不跳过 | 任何测试被跳过即整体失败；缺权限或缺环境时**阻塞并请求用户协助** |
| 不弱化 | 不得注释测试、降低断言、放宽超时来「解决」失败 |
| 可重复 | 连续 3 次完整运行必须全部通过；随机失败视为缺陷 |
| 隔离 | 独立临时目录、独立数据库文件、随机端口 |
| 确定性 | 不使用固定等待；用轮询 + 超时；时间经 `IClock`；随机固定种子 |
| 负向优先 | 安全能力必须有负向测试，断言「被拒绝」而非「不崩溃」 |
| 同阶段编写 | 测试与实现同阶段完成，不推迟 |
| 回归 | 每个修复的缺陷必须补回归测试并记录编号 |
| 覆盖率 | `Domain`/`Application` ≥ 85%；整体 ≥ 70% |
| 平台矩阵 | ConPTY、Job Object、服务宿主、路径、Java 探测须在两个 Windows 版本上验证 |

验证入口唯一：`eng/Validate.ps1`。**不得存在使整体通过的「跳过」路径。**

## 11. 安全规则

完整模型见 `docs/architecture/security-model.md`（41 个威胁）。不可违反的底线：

| 底线 | 说明 |
| --- | --- |
| 外部工作目录 | 永不递归删除；只有 `OwnedByServitium = true` 才允许删除目录 |
| 路径 | 九步校验链；**Agent 侧独立重新校验**，不信任控制平面传来的路径 |
| 进程 | 参数数组，绝不经 shell；高级原始命令的首参数必须是已登记的 `java.exe` |
| 进程树 | 每实例独立 Job Object，`KILL_ON_JOB_CLOSE` 且禁止 breakaway |
| 归档 | ZIP Slip、链接条目、炸弹、深度、非法名全部拦截 |
| 出站 | URL 下载须通过完整 SSRF 防护（含 DNS rebinding 与逐跳重定向重校验） |
| 授权 | 单一授权服务；REST、SignalR、文件流、Agent 指令四路径统一；权限变更 2 秒内生效 |
| CSRF | 任何端点不得关闭 |
| 审计 | 只追加不可修改；无更新与删除路径 |
| 秘密 | 不出现在日志、事件、响应、错误消息或诊断导出中 |
| 暴露 | 默认仅回环；非回环须受信 TLS 或可信反向代理模式 |
| HMCL manifest | `Cache-Control` 恒为 `no-cache`（否则玩家静默收不到更新，已运行验证） |
| HMCL overrides | 恒不压缩；强 ETag = 文件 SHA-1；支持 `Range` |

不得以「实现困难」为由降低任何安全要求。

## 12. 权限阻塞规则

需要下列操作而当前无权限时，**停止相关阶段并请求用户协助**：

安装 Windows 服务、修改服务恢复策略、设置系统 ACL、写入受保护目录、创建防火墙规则、安装 MSI、在干净系统执行安装测试。

**不得**：跳过、伪造通过、用静态审查代替、降低安全要求、删除该验收项。

请求时必须给出：阻塞阶段、需要执行的命令、遇到的错误、权限原因、用户需要做什么、系统影响、恢复步骤、清理方式。

同样适用于缺少环境组件（Windows App SDK、WebView2 Runtime、Java、第二个 Windows 平台、外部包源）的情况。

## 13. 动态记录规则

下列文件**应当**持续更新：

```text
docs/testing/acceptance-matrix.md
docs/testing/requirements-traceability.md
docs/records/current-status.md
docs/records/development-log.md
docs/records/decision-log.md
docs/records/problem-log.md
docs/records/validation-log.md
docs/records/change-requests.md
```

要求：

* 只记录事实、状态、证据与变更请求；
* **不能用于绕过受保护规格**；
* 日期使用绝对格式 `YYYY-MM-DD`；
* 验证记录必须写实际运行过的命令与结果，不得写预期值。

`decision-log.md` 只记录**实现级**决策（库选型、内部结构、算法实现方式）。触及产品范围、协议、界面契约、阶段顺序或安全要求的，一律走变更请求。

## 14. 受保护文档规则

下列内容**不得由编码 Agent 自行修改**：

```text
README.md
AGENTS.md
docs/index.md
docs/product/**
docs/reference/**
docs/architecture/**
docs/design/**
docs/engineering/**
docs/development/development-workflow.md
docs/development/master-plan.md
docs/development/risk-register.md
docs/development/phases/**
docs/testing/test-strategy.md
docs/testing/backend-acceptance.md
docs/testing/frontend-acceptance.md
docs/testing/user-testing-procedure.md
docs/delivery/delivery-plan.md
```

变更流程：

1. 在 `docs/records/change-requests.md` 新建条目，写明**当前要求**、**问题**、**建议**、**影响**；
2. 停止依赖该变更的工作，继续推进其余部分；
3. 在阶段报告中提示存在待裁决的变更请求；
4. 等待用户决定；
5. 未经用户批准不得修改基线。

`README.md` 与 `AGENTS.md` 除非用户明确要求，否则不得更新。

## 15. Git 规则

| 规则 | 说明 |
| --- | --- |
| 默认不提交 | 只有用户明确授权后才能执行 `git add` 与 `git commit` |
| 不推送 | 未经授权不得推送、打标签或发布 |
| 提交粒度 | 获得授权后按独立、可验证单元提交 |
| 提交前 | 必须通过对应测试 |
| 不提交 | 秘密、用户数据、构建产物、大型临时文件 |
| 分支 | 在 `develop` 上工作；不改写历史；不删除其他分支；不修改远程引用 |
| 提交信息 | 只允许单行标题；禁止正文、脚注与 Git trailer |

提交标题以 [Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/) 为结构依据，并采用 [commitlint `type-enum` 规则](https://commitlint.js.org/reference/rules.html#type-enum)列出的完整类型集合。仓库规则比通用规范更严格：每个提交只能有一行标题，必须填写 `scope`，不得在标题后添加空行、正文、脚注、`Signed-off-by`、`Co-authored-by` 或其他 trailer。

标题格式只有一种：

```text
type(scope): 中文摘要
```

`scope` 是必填项，用于指出提交主要影响的稳定领域。一次提交涉及多个领域时，选择最能代表变更目的的一个范围；无法选出单一主要范围，说明提交粒度过大，应先拆分提交。

允许的 `type` 只有以下 11 个英文小写词：

| type | 用途 |
| --- | --- |
| `build` | 构建系统或外部依赖变更 |
| `chore` | 不改变产品行为的日常维护 |
| `ci` | 持续集成配置与脚本变更 |
| `docs` | 仅文档变更 |
| `feat` | 新增功能 |
| `fix` | 修复缺陷 |
| `perf` | 性能改进 |
| `refactor` | 不新增功能也不修复缺陷的代码重构 |
| `revert` | 撤销既有提交 |
| `style` | 不影响行为的格式、空白或样式整理 |
| `test` | 新增或修正测试 |

`scope` 优先使用下列八个宏观中文词汇：

| scope | 适用范围 |
| --- | --- |
| `仓库` | 仓库整体结构、治理规则与通用配置 |
| `文档` | 产品、架构、设计、开发与记录文档 |
| `后端` | Domain、Application、Server、Agent、存储与协议实现 |
| `前端` | Web、Desktop、共享客户端与界面 |
| `工程` | 构建脚本、依赖、代码生成、开发环境与 CI |
| `测试` | 测试代码、夹具、验收与验证设施 |
| `安全` | 身份、授权、秘密、路径、归档及其他安全能力 |
| `交付` | 安装包、升级、卸载、签名、发布与交付流程 |

标题字符规则：

* `type` 必须取自上表，使用英文小写；
* `scope` 必须存在并优先取自上表。现有词汇确实无法准确表达时，可以新增同等宏观粒度的简短中文名词；不得为单个模块、功能、协议或页面建立细粒度 `scope`。只有固定技术名或产品名没有合适中文表达时，才使用英文单词；
* 一个标题只使用一个 `scope`，范围内部不添加顿号、逗号、斜杠或其他分隔符；
* 前缀中的 `(`、`)`、`:` 以及冒号后的空格必须使用 ASCII 半角字符；`type(scope):` 区域严禁使用中文全角标点；
* 冒号后的摘要必须使用中文；摘要需要标点时，只使用中文全角标点，不得混入英文逗号、句号、分号、冒号或括号；
* 标题只概括该提交的主要变动；验证结果、关联编号与详细背景写入对应动态记录，不写入提交信息。

示例：

```text
docs(文档): 统一参考实地考察、净室边界与提交规范
fix(后端): 修复重连后实例状态丢失问题
feat(前端): 增加节点连接状态筛选
```

## 16. 用户验收规则

| 规则 | 说明 |
| --- | --- |
| 何时请求用户 | 仅在阶段 24、权限阻塞、变更请求裁决、阶段文档明确标注的检查点 |
| 环境 | Development ASP.NET Core、Vite 开发服务器、Debug WinUI、独立测试数据库、独立测试目录、可见日志、可识别的进程与 PID、**不污染正式数据** |
| 请求内容 | 当前阶段、Agent 已完成的测试、启动的进程与 PID、地址、测试账号、测试数据、操作步骤、每步预期结果、日志位置、已知限制、停止方式、需要反馈的结果 |
| 禁止 | **不得只说「请测试」** |
| 判定 | 用户未明确确认通过时，不得标记用户验收完成 |
| 反馈 | 用户提出的问题记入 `docs/records/problem-log.md`；阻断与高严重度必须本轮修复并回归 |

## 17. 交付启动门

**开发完成不自动进入交付。**

交付启动必须**同时**满足：

1. 后端完整验收通过（阶段 18）；
2. Web 与 Desktop 的 Agent 自测通过（阶段 23）；
3. 用户在测试环境完成测试（阶段 24）；
4. 用户明确确认验收通过；
5. **用户随后明确要求开始交付**。

第 5 条独立于第 4 条。验收通过不等于授权交付。

交付流程见 `docs/delivery/delivery-plan.md`。

在用户于交付阶段明确选择之前，**不得创建 `.github/workflows/`**，不得编写任何 CI 配置。

## 18. 明确禁止的行为

| 禁止 | 理由 |
| --- | --- |
| 把参考实现的源码、资源、样式、图标、文案或品牌元素引入 Servitium | 违反净室原则并可能引入许可、协议或知识产权纠纷 |
| 未经记录与裁决，依据新证据改变产品、协议、界面、阶段或安全决定 | 实地考察不构成变更授权 |
| 修改 `docs/reference/**` 的证据来迎合实现 | 破坏事实基线 |
| 自行修改受保护文档 | 需用户批准 |
| 跳过测试或降低断言 | 破坏验收有效性 |
| 用静态审查代替实际运行 | 明确禁止 |
| 提前进入下一阶段 | 门禁存在的意义 |
| 在阶段 18 通过前开始正式前端开发 | 硬门禁 |
| 自动进入交付 | 交付需用户明确启动 |
| 未经授权提交、推送、打标签、发布 | Git 规则 |
| 引入被排除的能力 | 产品边界 |
| 引入 Fluent 之外的 UI 组件库 | 界面基线 |
| 让实例进程经 shell 启动 | 命令注入 |
| 递归删除外部工作目录 | 不可逆的数据损失 |
| 在仓库中保留临时脚本 | 需要长期保留的检查逻辑放入 `eng/` |

## 19. 阶段报告格式

每完成一个阶段，按此格式报告：

```text
阶段：<编号与名称>
状态：已完成 / 部分完成 / 阻塞
产物：<新增与修改的文件>
实现要点：<简述>
测试：<运行的命令与结果>
验证：<Validate.ps1 阶段结果>
更新的记录：<文件列表>
完成条件核对：<逐条>
未完成项与原因：<如有>
待裁决的变更请求：<如有>
下一阶段的前置条件：<是否满足>
```
