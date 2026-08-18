# 开发日志

本文件是**可持续更新**的动态记录，按时间顺序记录每次有实质进展的工作。

由后续编码 Agent 维护。只记录事实，不能用于绕过受保护规格。

## 1. 记录格式

每条记录使用下列结构：

```text
## YYYY-MM-DD — 阶段 <编号> <名称>

**做了什么**
- 逐条列出实质工作

**产物**
- 新增或修改的文件（相对仓库根）

**验证**
- 实际运行的命令
- 实际结果

**遗留**
- 未完成项与原因（无则写「无」）
```

要求：

* 日期使用绝对格式，不使用「今天」「上周」等相对表述；
* 验证一栏必须写实际运行过的命令与结果，不得写「应当通过」；
* 一次工作跨多天时按天分条。

---

## 2026-08-02 — 阶段 00 文档基线

**做了什么**

- 核查仓库状态：`develop` 分支为未产生提交的全新分支，工作树仅含 `.git`，另存在只读的 `legacy` 分支。
- 调查 MCSManager（`master` @ `072fc596767d9719ca925085b91c0df56caffa24`，`v10.17.0-15`，工作树干净）：
  - 全部 27 条前端路由与 24 个默认布局页面条目；
  - 全部 76 个前端页面级与对话框级组件；
  - 全部 17 个面板 HTTP 路由文件（89 个端点）与 11 个节点 Socket 路由文件（51 个事件）；
  - 权限、审计、文件、计划任务、Java、模组等服务；
  - 实例配置、守护进程配置、生命周期与状态常量；
  - 通过语言包解码界面文案以确定功能语义。
- 调查 HMCL（`main` @ `d297417909208deb3a4bb8378af4769f97de44c8`，`v3.17.0.351-37`，工作树干净）：
  - `ServerModpackManifest`、`ServerModpackProvider`、`ServerModpackCompletionTask`、`ServerModpackRemoteInstallTask`、`ServerModpackLocalInstallTask`、`ServerModpackExportTask`；
  - `ModpackConfiguration`、`ModpackInstallTask`、`MinecraftInstanceTask`、`ModpackUpdateTask`、`ModAdviser`；
  - `FetchTask`、`GetTask`、`FileDownloadTask`、`NetworkUtils`、`CacheRepository`；
  - `LibraryAnalyzer`、`GameBuilder`、`DefaultGameRepository`、`HMCLGameRepository`；
  - `LauncherHelper`、`ModpackHelper`、`ModpackSelectionPage`、`RemoteModpackPage`。
- 对 HMCL 协议进行**运行验证**：用 `javac` 直接编译 `HMCLCore` 的 430 个源文件到临时目录（依赖取自本机 Gradle 缓存，通过一次性 init script 导出运行时 classpath），在临时目录编写探针程序启动本地 HTTP 服务并驱动 HMCL 自身的 `ServerModpackCompletionTask` 执行，观察真实 HTTP 请求与文件系统结果。HMCL 仓库未被修改，未新增任何文件。
- 撰写 57 份文档：仓库根 2 份（`README.md`、`AGENTS.md`）+ `docs/` 下 55 份。

**产物**

- `README.md`、`AGENTS.md`
- `docs/index.md`
- `docs/product/`：`product-definition.md`、`decision-baseline.md`、`functional-specification.md`（262 条需求）、`hmcl-distribution-specification.md`、`localization-specification.md`
- `docs/reference/`：`source-evidence-index.md`、`mcsmanager-feature-inventory.md`（304 条）、`mcsmanager-behavior-matrix.md`（17 个场景）、`mcsmanager-frontend-layout.md`（逐页）、`hmcl-auto-update-protocol.md`、`hmcl-client-behavior.md`（22 个场景）
- `docs/architecture/`：`system-architecture.md`、`data-and-storage.md`、`protocols-and-contracts.md`、`security-model.md`（41 个威胁）、`repository-and-solution-layout.md`
- `docs/design/`：`information-architecture.md`（30 个页面）、`shared-ui-specification.md`、`desktop-specification.md`、`web-specification.md`
- `docs/engineering/`：`engineering-standards.md`、`development-environment.md`、`build-and-validation.md`
- `docs/development/`：`development-workflow.md`、`master-plan.md`、`risk-register.md`、`phases/` 下 15 份阶段文档
- `docs/testing/`：`test-strategy.md`、`backend-acceptance.md`、`frontend-acceptance.md`、`user-testing-procedure.md`、`acceptance-matrix.md`、`requirements-traceability.md`
- `docs/records/`：本文件及 `current-status.md`、`decision-log.md`、`problem-log.md`、`validation-log.md`、`change-requests.md`
- `docs/delivery/delivery-plan.md`

**验证**

- 两个参考仓库的分支、提交与工作树状态已实际读取并记录在 `docs/reference/source-evidence-index.md`。
- HMCL 协议运行验证的完整过程与观察结果记录在 `docs/records/validation-log.md`；结论写入 `hmcl-auto-update-protocol.md` 与 `hmcl-client-behavior.md` 并标注 `[RUNTIME]`。
- 文档一致性检查（链接、空文件、需求编号、命名、占位词、覆盖）结果记录在 `docs/records/validation-log.md`。

**遗留**

- 无。阶段 00 的完成条件全部满足。
- 本轮未创建任何源码、测试、工程脚本、安装器或 CI 目录；未执行 `git add`、`git commit`、`git push`。

---

## 2026-08-03 — 阶段 00 基线一致性复检

**做了什么**

- 消除阶段 10 文档中的两处表述歧义：Web 与 Desktop 项目只在阶段 20–22 创建，阶段 10 仅创建可编译的 `Servitium.Client` 骨架；既有追溯与验收矩阵在后续阶段按实现证据更新，不重新建立或覆盖。
- 对全部 57 份文档重新实际执行文档一致性检查（不复用 2026-08-02 的结论），共 23 项检查，结果记录在 `validation-log.md`。
- 核对动态记录与实际状态一致：阶段 00 已完成、下一阶段为 10、尚未编写产品代码、尚未开始阶段 10、全部需求处于「未开始」、两份矩阵中无虚假的实现或测试通过记录。

**产物**

- 修改：`docs/development/phases/10-backend-foundation.md`（第 5 节、第 21 节）
- 修改：`docs/records/validation-log.md`、`docs/records/development-log.md`、`docs/records/current-status.md`

**验证**

- 会话临时目录中的 PowerShell 7 检查脚本：23 项检查全部通过，失败项 0，提示项 3 类共 7 条；覆盖 57 个文件、11 个目录、10 条 Markdown 链接、533 条反引号路径引用、262 条需求编号。脚本按临时脚本规则未进入仓库。
- `git status --short`、`git diff --cached --stat`、`git diff --cached`：待提交内容仅为 `AGENTS.md`、`README.md` 与 `docs/**`，无源码、测试、构建产物、密钥或临时文件。
- `D:\GitHubPublic\MCSManager` 与 `D:\GitHubPublic\HMCL` 工作树在检查前后均干净，提交与 `source-evidence-index.md` 登记的快照一致。

**遗留**

- 三条提示项已在 `validation-log.md` 中登记，均不影响阶段 00 的完成条件，也不阻塞阶段 10：阶段 17 文档含 2 个附加章节；5 处阶段名称详略措辞差异；`README.md` 提及的许可文件尚未加入仓库。三者都需修改受保护文档或新增仓库根文件，按变更请求流程由用户裁决。

---

## 2026-08-03 — 阶段 00 文档基线全量复核

**做了什么**

- 按 `AGENTS.md` 必读顺序通读并复核仓库当前 57 份 Markdown；未读取 MCSManager 或 HMCL 参考仓库。
- 按完整章节重写补丁式区域，并同步所有受影响的产品、参考、架构、设计、阶段与测试契约。
- 补齐 8 条只有标题而无要求正文的需求；把追溯矩阵的范围占位展开为 262 条逐需求记录。
- 修正 HMCL 可变 Channel 内容 URL 的缓存契约、OIDC SSRF 落地阶段、Agent 运行账户凭据边界、Agent 重启语义、项目依赖方向与威胁测试索引。
- 把阶段 17 收拢为标准 21 节，统一五处阶段名称，展开阶段 13–16 的动态记录清单。
- 重写验证入口的渐进登记与门禁 Profile，改为在临时目录逐文件校验生成产物，消除对整个脏工作树执行 `git diff` 的错误要求。
- 修正 MCSManager 清单的路由、分类与总数统计；修正 README 许可状态；统一全部 Markdown 为 UTF-8 无 BOM、LF 行尾。

**产物**

- 修改：`README.md` 与 `docs/` 下涉及产品、参考、架构、设计、工程、开发、测试及动态记录的文档。
- 仓库外审查快照：`C:\Users\RavenYin\AppData\Local\Temp\servitium-doc-review-20260803-before`；完成前后逐文件比较后删除。

**验证**

- `C:\Users\RavenYin\AppData\Local\Temp\servitium-doc-audit-20260803.ps1`：19/19 项通过，覆盖文件范围、空文档、行尾、内部链接、需求定义与逐条追溯、威胁索引、阶段结构与名称、参考清单统计、HMCL 缓存契约和 Git 状态。
- `git status --short --branch`：`develop` 尚无提交；`AGENTS.md`、`README.md`、`docs/` 未跟踪。
- `git diff --cached --quiet`：退出码 0，暂存区为空。

**遗留**

- 无。PB-001 已关闭。
- 未执行 `git add`、`git commit`、`git push`。

## 2026-08-03 — 参考证据与 Git 治理统一改写

**做了什么**

- 整体重写 `AGENTS.md` 及相关受保护文档中的参考考察和提交规则。
- 移除治理文档中的具体模型名称，使规则面向全部编码 Agent。
- 将 `docs/reference/**` 统一定位为带提交与验证级别的证据快照；保留原有 MCSManager 功能清单、行为矩阵、页面布局证据以及 HMCL 协议与客户端行为文档。
- 明确鼓励编码 Agent 在实现、测试与兼容性排查中按需前往 `D:\GitHubPublic\HMCL` 与 `D:\GitHubPublic\MCSManager` 实地考察，同时统一只读隔离、证据记录、决策裁决与净室要求。
- 将 Git 提交规范收拢为单行标题：采用 `build`、`chore`、`ci`、`docs`、`feat`、`fix`、`perf`、`refactor`、`revert`、`style`、`test`，禁止正文、脚注与 trailer，结构标点使用 ASCII 半角形式，摘要使用中文及中文标点。

**产物**

- 修改：`AGENTS.md`、`docs/index.md`、`docs/development/development-workflow.md`、阶段 00 与阶段 17 文档。
- 修改：`docs/reference/` 下六份参考证据文档。
- 修改：工程规范、变更请求、当前状态、开发日志与验证日志。

**验证**

- PowerShell 内联只读检查覆盖全部 57 份 Markdown：文件非空、UTF-8 无 BOM、LF 行尾、无行尾空白、内部链接可解析，问题 0。
- 全库模式检查确认具体模型名称、封闭式参考调查措辞、旧中文提交类型和旧正文模板均无命中。
- Git 类型集合与 commitlint 当前 `type-enum` 的 11 个词逐项一致；三个核心治理文档均明确单行标题与禁止正文规则。

**遗留**

- 无。
- 未执行 `git add`、`git commit`、`git push`。

## 2026-08-03 — Git scope 规则整体修订

**做了什么**

- 将提交标题收敛为唯一结构 `type(scope): 中文摘要`，`scope` 从可选项改为必填项。
- 将优先范围词收敛为 `仓库`、`文档`、`后端`、`前端`、`工程`、`测试`、`安全`、`交付` 八个宏观类别；必要时新增的范围也必须保持同等粒度。
- 规定每个标题只能选择一个主要范围；无法选择时先拆分提交，不通过多个范围拼接掩盖过大的提交粒度。
- 删除其他提交标题变体，使 Git 规则、工程规范与开发工作流保持同一表述。

**产物**

- 修改：`AGENTS.md` 的 Git 规则完整章节。
- 同步：`docs/engineering/engineering-standards.md`、`docs/development/development-workflow.md` 与动态记录。

**验证**

- PowerShell 内联只读检查覆盖 57 份 Markdown，确认 `scope` 必填规则在三个核心治理文档中一致。
- 八个优先范围词全部为中文；唯一标题格式与三个中文范围示例均通过结构检查。
- 已移除旧的范围省略格式及额外标题变体；编码、行尾与内部链接检查问题 0。

**遗留**

- 无。
- 未执行 `git add`、`git commit`、`git push`。

---

## 2026-08-03 — 本地整合包发布原型参考基线

**做了什么**

- 登记 `E:\Minecraft\MinecraftServersDuoyun\test`，供开发期间按需核实本地整合包发布流程。
- 只读检查根 README、`scripts/`、`public/` 与 `config/` 的结构证据，整理 Prism 导出包识别、候选内容、不可变 Release、稳定 Channel、显式发布与 HMCL/Prism 派生行为。
- 新增独立参考文档，并同步 Agent 治理、文档索引、开发工作流、阶段 00、证据索引、变更请求和当前状态。
- 明确参考路径只读；原型中的真实 ZIP、Release、日志、运行状态和备份属于用户数据，不读取、不执行、不用作样本。
- 净室边界限定为可观察行为、文件格式事实与问题拆分；Servitium 的代码、数据模型、契约、界面和文案独立重建。

**产物**

- 新增：`docs/reference/local-pack-publishing-prototype.md`。
- 同步：`AGENTS.md`、`docs/index.md`、`docs/development/development-workflow.md`、`docs/development/phases/00-documentation-baseline.md`、`docs/reference/source-evidence-index.md`、`docs/records/change-requests.md`、`docs/records/current-status.md`。

**验证**

- 内联 PowerShell 只读检查：58 份 Markdown、262 条需求与 262 条追踪记录、15 份阶段文档与 7 份参考文档全部通过，问题 0。
- 本地原型证据清单复算为 15 个文件、121535 字节，SHA-256 为 `857b6d8c5fffa0644e0c47351dbef2908a2fa97a8ee8c6322caac82fc78c2787`，与登记快照一致。
- HMCL 与 MCSManager 参考仓库工作树干净；Servitium 暂存区为空、仍未产生 `HEAD`。

**遗留**

- 无。
- 未执行 `git add`、`git commit`、`git push`。

---

## 2026-08-03 — Prism 实例导出证据与文档语言复核

**做了什么**

- 在只读状态下检查 Prism Launcher `7174c26a90662fdedf736b7457b13a1a25f81f4e` 的原生实例导出、ZIP 写入、文件过滤、组件清单、游戏目录选择与导入识别调用链。
- 新增 Prism 原生实例 ZIP 格式文档，区分“导出实例”与 Modrinth、CurseForge 平台格式导出，并记录 `instance.cfg`、`mmc-pack.json`、游戏目录和转换信任边界。
- 将 `D:\GitHubRecRivenVI\PrismLauncher` 登记到 Agent 治理、文档索引、开发工作流、阶段 00 与参考证据索引。
- 统一专题参考文档的开篇结构，把取证、只读、净室与决定变更规则集中到参考证据索引，删除重复的自我定位和对话式修订说明。
- 登记并关闭 PB-002；复核其余 Markdown 的开篇、长句、重复句和修订过程措辞。

**产物**

- 新增：`docs/reference/prism-instance-export-format.md`。
- 修改：`AGENTS.md`、`docs/index.md`、开发工作流、阶段 00、参考证据文档与动态记录。

**验证**

- 全量 Markdown 只读检查：59 份文件均非空、UTF-8 无 BOM、LF 行尾、无行尾空白、单一围栏外 H1、围栏成对且内部链接可解析；262 条需求与 262 条追踪记录集合一致；15 份阶段文档各含 21 个二级章节；8 份参考文档均已登记。
- 模式与开篇检查：具体模型名称、封闭式调查结论、阻止实地考察的旧规则、重复层级说明和对话式授权措辞无命中；7 份专题参考文档均在开篇登记证据快照。
- HMCL、MCSManager 与 Prism Launcher 工作树干净且 HEAD 与证据索引一致；本地原型的 15 个证据文件、121535 字节与清单 SHA-256 均未变化。
- Servitium 仍未产生 `HEAD`，59 份 Markdown 未跟踪，暂存区为空。

**遗留**

- 无。
- 未执行 `git add`、`git commit`、`git push`。

---

## 2026-08-03 — 阶段 10 后端工程基础

**做了什么**

- 建立 .NET 10 解决方案、7 个生产项目、6 个测试项目与 Java 测试夹具项目，落实中央包版本、锁定还原、分析器、警告即错误和统一产物目录。
- 建立 Server 与 Agent 的最小 Windows 服务兼容宿主、健康端点、启动日志、PID 文件和 Agent 自检入口。
- 建立 60 个稳定错误码、Agent 控制协议骨架、项目引用方向检查、主机集成测试和可控 Java 服务端夹具。
- 建立构建、测试、格式、覆盖率、依赖漏洞、文档、生成一致性与统一验证脚本；修复验证入口、集成测试边界与覆盖率聚合问题。

**产物**

- 新增根工程配置：`global.json`、`Directory.Build.props`、`Directory.Build.targets`、`Directory.Packages.props`、`NuGet.config`、`.editorconfig`、`.gitattributes`、`.gitignore`、`Servitium.sln`。
- 新增生产项目：`src/Servitium.Domain`、`Servitium.Application`、`Servitium.Contracts`、`Servitium.Infrastructure`、`Servitium.Client`、`Servitium.Server`、`Servitium.Agent`。
- 新增测试与夹具：`tests/Servitium.*Tests`、`tests/Fixtures/Servitium.JavaServerFixture`。
- 新增工程入口：`eng/*.ps1`、`eng/coverage.runsettings`。
- 修改动态记录与测试矩阵。

**验证**

- `dotnet restore --force-evaluate`：通过，所有项目生成锁文件。
- `dotnet build Servitium.sln --configuration Release --no-restore`：通过，0 警告、0 错误。
- `./eng/Check-Docs.ps1`：通过，57 份 `docs/` Markdown、262 条需求、387 个验收项一致。
- `./eng/Validate.ps1`：连续 3 次通过；证据目录为 `artifacts/test-results/validate-20260803-052508/`、`validate-20260803-052621/`、`validate-20260803-052718/`。
- 架构测试 7/7、Server 集成测试 1/1、Agent 集成测试 2/2、Java UNT-001 与 INT-003 全部通过；无跳过。
- 覆盖率聚合结果为 100%；依赖漏洞扫描无已知漏洞。该覆盖率仅代表阶段 10 当前可执行生产代码，不代表后续后端能力。

**遗留**

- 阶段 18 `BackendGate`、管理员环境、第二 Windows 平台、真实样本与用户验收均未执行，状态保持未开始。
- 未执行 `git add`、`git commit`、`git push`。

---

## 2026-08-03 — 阶段 11 Domain、Application 与 Contracts

**做了什么**

- 实现领域基础、身份与唯一 Owner、节点、Java、实例状态机、无 shell 启动参数、工作目录归属、计划任务、不可变 HMCL Release/Channel、长操作、审计、路径和系统设置约束。
- 实现 Application 端口、单一授权服务、稳定错误结果、Base64URL 复合键游标，以及四项核心流程进入后续基础设施前的用例编排边界。
- 建立跨领域 DTO、22 类实时事件、完整 `AgentControl.v1` 双向流和 54 个 Agent 指令枚举。
- 建立覆盖 30 个页面编号的机器可读界面契约，以及含 60 个稳定错误码文案的 95 键 `zh-Hans`/`en-US` 单一资源来源。
- 扩展生成检查、单元测试、架构测试和覆盖率采集；修复 Owner、审计不可变性、协议解析、编码格式和覆盖率问题。

**产物**

- 新增与修改：`src/Servitium.Domain/**`、`src/Servitium.Application/**`、`src/Servitium.Contracts/**`。
- 新增测试：`tests/Servitium.Domain.Tests/DomainRuleTests.cs`、`SupportingDomainModelTests.cs`、阶段 11 架构断言；`tests/Servitium.Application.Tests/**`。
- 修改工程入口：`Directory.Packages.props`、Contracts 项目文件、`eng/Generate.ps1`、`eng/Test.ps1`、`eng/Check-Coverage.ps1`、`eng/coverage.runsettings`。
- 更新动态记录、需求追踪与验收矩阵。

**验证**

- `dotnet build Servitium.sln --configuration Release --no-restore --maxcpucount:1`：通过，0 警告、0 错误，protobuf C# 生成成功。
- `./eng/Generate.ps1 -Verify`：60 个错误码、95 个双语键、30 个页面、22 个事件、54 个 Agent 指令一致。
- `./eng/Test.ps1 -Category Unit -Configuration Release`：Domain 30/30、Application 6/6、Java UNT-001 通过；无跳过。
- `./eng/Test.ps1 -Category Architecture -Configuration Release`：Domain 11/11、Application 1/1 通过；无跳过。
- `./eng/Check-Coverage.ps1 -Configuration Release`：整体 88.07%，Domain 90.29%，Application 97.03%；未降低阈值。
- `./eng/Validate.ps1 -Profile Development`：连续 3 次通过；证据目录 `validate-20260803-060512/`、`validate-20260803-060559/`、`validate-20260803-060642/`。

**遗留**

- 所有跨阶段需求保持“进行中”：REST、数据库、Agent、进程、内容库与客户端消费由后续阶段完成。
- 阶段 18 `BackendGate`、真实样本、管理员环境、第二 Windows 平台和用户验收均未执行。
- 未执行 `git add`、`git commit`、`git push`。

---

## 2026-08-03 — 阶段 12–17 核心后端纵向链路与受限 WinUI 测试工作台

**做了什么**

- 建立控制平面 SQLite 迁移与备份、Owner 初始化、Argon2id 密码登录、持久会话、API Key、可选 TOTP、CSRF、基础授权、安全响应头、审计中间件与 REST/gRPC 宿主。
- 建立节点一次性令牌、原子注册、长期凭据、心跳、轮换/吊销、Agent 本地 SQLite 与 DPAPI 凭据保存；隔离测试以两个回环端口分别承载 REST 和 Agent HTTP/2 gRPC。
- 建立 Java 有界探测、纯读取列表和手工绝对路径登记，区分显式扫描与无副作用刷新；建立实例自有目录/外部绝对目录登记、Server ZIP 安全传输与解压、外部目录永不递归删除边界。
- 建立 EULA、结构化 Java 参数、无 shell 启动、Windows Job Object、1 MiB/5000 行输出缓冲、命令输入、停止降级、强停、崩溃持久化与宿主恢复。
- 建立受限文件列表、容量、新建、复制、移动、非递归删除、UTF-8 读取与同目录原子写入，以及 `properties` 保留 BOM/换行/注释/顺序/分隔符的解析基础。
- 建立操作日志与安全审计只追加 SQLite 存储、只读查询和 UTF-8 BOM CSV 导出。
- 建立合成归档与 Prism 原生 ZIP 识别/预览/固定过滤/转换、SHA-1 内容寻址、不可变 Release、发布 diff、原子 Channel 指针、HMCL manifest 与 overrides 的 ETag、304、Range、HEAD 和恒不压缩契约。
- 依据当前会话用户决定，新增仅覆盖四项核心流程的 WinUI 3 单窗口测试工作台，包括测试 Server 连接、初始化/登录、节点与 Channel 选择、文件/目录选择、进度、稳定错误码和结果链接；没有创建 Web 或完整 Desktop 信息架构。

**主要产物**

- 后端：`src/Servitium.Infrastructure/{Persistence,Identity,Nodes,Networking,Distribution,Auditing}`、`src/Servitium.Server/{Security,Hosting,AgentGateway,Endpoints,Hmcl}`、`src/Servitium.Agent/{Storage,ControlPlane,Java,Instances,Paths,Archives,Lifecycle,Processes,Terminals,Files,Configurations}`。
- 契约与客户端：`src/Servitium.Contracts/{Identity,Nodes,Java,Instances,Files,Distribution,Agent}`、`src/Servitium.Client/ServitiumTestClient.cs`。
- 测试工作台：`src/Servitium.Desktop`，目标为 unpackaged WinUI 3 x64，含 `zh-Hans` 与 `en-US` 资源。
- 数据库迁移：Server 初始控制平面与 HMCL 分发表；Agent 初始本地存储与实例运行时状态扩展。
- 自动化：Infrastructure、Server、Agent 集成测试，合成 Java 服务端 JAR，以及覆盖率路径归一和基础设施测试采集。

**已运行验证**

- Release 全解决方案构建通过：15 个项目，0 警告、0 错误；WinUI 3 x64 产物成功生成。
- 单元测试：Domain 30/30、Application 6/6、Java UNT-001 通过；架构测试 Domain 11/11、Application 1/1 通过。
- 集成测试：Infrastructure 18/18、Server 7/7、Agent 38/38、Java INT-003 通过，无跳过；INT-305 与进程纵向测试共同覆盖 Java 列表纯读取协议。
- 独立 Server/Agent 进程级测试实际完成节点注册、Java 登记、外部目录附加、含合成 Java JAR 的 Server ZIP 导入、EULA、启动、字面命令输入、停止和受保护路径拒绝。
- 生命周期短进程回归连续 3 次通过；覆盖率采集全部通过，整体 73.79%，Domain/Application 均满足 85%。
- WinUI 连接启动探针以独立 Server PID 71884 和 Desktop PID 110648 运行后退出码为 0；两个进程均已在探针结束后停止，数据位于忽略的 `artifacts/test-runs/winui-selftest-4796693d0a4e45c8b25ec4a8b6e1eeeb`。
- 依赖漏洞扫描对 15 个项目无已知漏洞；生成验证为 60 错误码、95 双语键、30 页面、22 事件、54 Agent 指令一致。

**正式缺口与边界**

- 阶段 12–17 均只标记部分完成；OIDC、本机身份交换、完整权限即时失效、Java 归档导入、ConPTY/RCON/指标、完整文件传输与配置格式、模组、计划任务、完整设置、私有 Channel、回滚/GC/统计等尚未完成。
- 未读取、打开、解压、登记、转换或运行任何用户真实 ZIP、实例目录、Prism 包、HMCL 客户端/服务器数据；四项真实测试等待用户在隔离环境执行。
- 未执行阶段 18 `BackendGate`、管理员服务/ACL/防火墙/MSI 测试、第二 Windows 平台矩阵、阶段 23、阶段 24 或交付；均不得视为通过。
- 未执行 `git add`、`git commit`、`git push`、打标签或发布。

**交接收敛**

- 启动隔离交接环境时发现 Java 列表查询误发扫描指令，登记 PB-020；新增 `LIST_JAVA` 纯读取协议、Server 指令级回归和进程纵向回归后关闭。
- 修复后 `./eng/Validate.ps1 -Profile Development -Configuration Release` 连续 3 次通过；Server 集成测试增至 7/7，整体覆盖率为 74.00%。
- 交接运行形态使用 Development Server、Development Agent 与 Debug x64 WinUI 工作台；仍不构成阶段 18、阶段 23 或阶段 24 验收。

## 2026-08-03 — 撤销真实样本检查点并恢复正式开发

**做了什么**

- 以当时的 `develop` 检出重新核对分支、`HEAD`、暂存区、工作树、源码、动态记录、构建入口与遗留进程；上一份交接报告仅作为线索。
- 用户将四项真实样本测试统一延后到阶段 12–17 后端生产代码、共享客户端与正式 WinUI 3 Desktop 代码完成之后；该调整只改变用户介入时间，不降低正式 Gate、安全或测试要求。
- 现有受限测试工作台降为 Desktop 实现起点；允许继续正式 Desktop，但不启动 Web、交付、安装包、CI 或发布。
- 已停止上一轮隔离环境遗留的 `Servitium.Server` PID 92896 与 `Servitium.Agent` PID 62720；未删除隔离数据，未读取任何真实样本。

**产物**

- 更新阶段 12–17 生产实现、`Servitium.Client` 与正式 Desktop 的工作计划和动态状态。

**验证**

- 核对当时检出的源码、测试和进程；该条记录本身不构成阶段 Gate 证据。

**遗留**

- 当时审计识别的实现缺口随后已大部分补齐；当前事实以 2026-08-04 的代码与记录为准。

## 2026-08-03 — 归档上传请求体限制修复

- 用户协作测试报告三个归档上传入口均显示 `Error while copying content to a stream.`。只读检查隔离 Server 日志确认请求在进入归档处理前被 Kestrel 的 30000000 字节默认请求体上限拒绝；未打开或解压用户真实 ZIP。
- 登记并关闭 PB-021。三个归档端点现在各自声明 8 GiB 文件上限和 16 MiB multipart 开销，表单解析与 Kestrel 请求体限制采用同一策略；超限映射为 HTTP 413 与稳定错误码 `file-too-large`，非归档端点的默认限制不变。
- 将 HMCL 归档解压后总量从旧实现的 20 GiB 收敛到受保护安全基线要求的默认 8 GiB；没有放宽 ZIP Slip、链接、压缩比、条目数、深度或非法名称校验。
- 新增 SEC-166、SEC-167、INT-501、INT-1300 与 INT-1310 回归证据。自行生成的 31 MiB 以上 ZIP 已分别贯通 Server/Agent 服务端导入、独立 Kestrel Prism 预览和 HMCL Release 上传。
- 独立进程测试夹具曾两次因未以命令行覆盖默认监听地址而超时，随后改为显式参数数组启动随机回环端口并通过；这些失败未计为验证通过。

## 2026-08-04 — 正式契约与 Desktop 缺陷登记

**做了什么**

- 重新核对当前 `develop`、`HEAD`、未暂存工作树和遗留进程，并按规定顺序重读必读基线与阶段 12–17 文档。
- 以当前代码和实际验证结果纠正动态状态中 OIDC、ConPTY、RCON、文件、模组、计划与 HMCL 生命周期“未完成”的过时描述。
- 登记 PB-022 至 PB-028：OpenAPI 正式响应契约、通知暂停、Prism Release 工作流、Desktop 核心页面、本地化、覆盖率与动态记录结构问题。
- 把本文件的说明与记录格式恢复到正文之前，并把旧的恢复开发记录放回时间序列。

**产物**

- `docs/records/problem-log.md`
- `docs/records/current-status.md`
- `docs/records/development-log.md`
- `docs/records/validation-log.md`

**验证**

- 当前检出核对：`develop`，`HEAD` 为 `706a7726e5066ec01e4f5028117326b22c93baeb`，暂存区为空，无 Servitium Server、Agent 或 Desktop 遗留进程。
- 复核并记录 `validate-20260804-045136/` 与 `validate-20260804-050049/` 的实际结果；整体覆盖率 64.84% 的失败保持开放。

**遗留**

- PB-022 至 PB-028 均未关闭；阶段 18、23、24 均未通过，四项真实样本测试均未执行。

## 2026-08-04 — 后端、协议与正式 Desktop 代码收敛

**做了什么**

- 为 145 个 OpenAPI 路径、181 个操作补齐真实请求、成功状态、响应模型、文件流与 Problem Details 契约；生成 Client 操作清单并建立 Server/OpenAPI/Client 双向覆盖检查。
- 完成通知 30 分钟、1 小时、直到本次进程、取消、暂停队列、恢复与设置重载语义；进程内暂停不再跨启动恢复。
- 把 Prism 预览/转换从 PG-019 实例创建迁至 PG-062 HMCL Release 创建；后端负责归档安全、候选/排除、元数据、差异与最终确认。
- 完成 30 页 Desktop 差异审计和核心交互：正式五步实例向导、文件多标签/批量/剪贴板/上传下载/预览、节点令牌、用户批量授权与撤销、活动审计过滤导出、稳定值本地化和页面权限预检查。
- 为身份应用服务、Agent 指令分派、Server 实例/文件/配置/模组/计划纵向端点补充行为测试；修复下载失败覆盖既有目标、生命周期输入流及退出监视竞态。
- 按当前代码逐项更新 262 条需求追溯，并在验收矩阵中分开记录代码、自动测试、正式 Gate 和用户证据。

**产物**

- `src/Servitium.Server/OpenApi/`、Server 端点映射、`src/Servitium.Client/Generated/GeneratedProtocolContract.g.cs`
- `src/Servitium.Desktop/Models/DesktopPageAuthorization.cs`、Desktop 服务、视图和动作分派
- `tests/Servitium.Application.Tests/IdentityApplicationServiceTests.cs`
- `tests/Servitium.Agent.IntegrationTests/AgentCommandDispatcherTests.cs`
- `tests/Servitium.Server.IntegrationTests/CoreEndpointWorkflowTests.cs`
- `docs/records/desktop-page-gap-audit.md`

**验证**

- `eng/Generate.ps1 -Verify` 通过：145 路径、181 操作、60 错误码、771 本地化键、30 页面、22 事件、54 Agent 命令、2 份 Desktop 资源一致。
- `eng/Check-Coverage.ps1 -Configuration Release` 通过：Domain 41、Application 11、Infrastructure 36、Server 51、Agent 187；整体行覆盖率 70.83%，Domain 与 Application 均达到 85%。产物为 `artifacts/test-results/coverage/20260804-131514/`。
- Client 13/13、Desktop 36/36；生命周期崩溃和自动重启回归连续 5 轮通过；Release 解决方案构建 0 警告、0 错误。
- `eng/Check-Docs.ps1` 在记录集中更新前通过：58 份 Markdown、262 条需求、387 个验收项一致；最终记录落盘后仍需复跑。

**遗留**

- 仍需连续 3 次完整 Development 验证和隔离启动检查；阶段 18、23、24、真实样本、管理员权限及第二 Windows 平台验证均未执行。

**完整验证稳定性修正**

- 前两次完整 Development 验证通过；第 3 次在 INT-805 的隔离目录清理中发现 PB-034，因此连续通过计数作废并从修复后重新开始。
- PB-034 的根因是测试在 `WebApplicationFactory` 释放前删除仍包含打开 SQLite 的目录；改为先异步关闭宿主、再删除目录。该测试族连续 5 轮共 10/10 通过，未增加重试删除或固定等待。

- 为进程级 Desktop 自测增加仅在 `--self-test` 下使用的 `SERVITIUM_DESKTOP_TEST_DATA_ROOT`；普通启动不读取该变量。首次尝试直接在 xUnit 构造 `AppServices` 触发 WinRT `ResourceLoader` 环境错误，登记 PB-035 后改为纯路径解析测试，Desktop 组合根交由真实进程自测。

## 2026-08-04 — 最终连续验证与隔离进程自检

**做了什么**

- 以修复 PB-034、PB-035、PB-036 后的工作树从零执行完整 Development 验证；随后修复 PB-037、PB-038 后再次从零计数，最终连续三次报告为 `artifacts/test-results/validate-20260804-151435/`、`validate-20260804-151831/`、`validate-20260804-152227/`。中间的 `validate-20260804-151029/` 因 PB-036 单次时序失败未计入。
- 发现未打包 WinUI 进程的语言覆盖和 ResourceLoader 默认资源上下文问题，登记并关闭 PB-037、PB-038；语言覆盖改为受限回退，本地化在资源上下文不可用时从共享 `Localization/{language}.json` 加载。

**验证**

- 最终连续三次完整验证的 12 个注册阶段均通过且无跳过；阶段 17 的覆盖率日志实际记录 Domain 41、Application 11、Infrastructure 36、Server 51、Agent 127，整体 70.85%（中间一次 70.88%），Domain/Application 均满足 85%。
- `dotnet build tests/Servitium.Desktop.Tests/Servitium.Desktop.Tests.csproj -c Release --property:Platform=x64 --no-restore` 通过；Desktop 测试 38/38，无跳过。
- 隔离 Server 使用显式 `--urls http://127.0.0.1:1253` 启动，PID 72924，健康检查 200，15 个数据库迁移完成；首次初始化请求无 CSRF 时实际返回 400 `csrf-token-missing`，随后通过 `/api/v1/auth/csrf` 与同一 Cookie/请求头创建合成所有者账号。
- 隔离 Agent 使用 `--environment Development --self-test` 运行，PID 85840，退出码 0；隔离 Desktop 使用 `--server http://127.0.0.1:1253 --self-test` 运行，PID 118520，退出码 0。Desktop 使用 `SERVITIUM_DESKTOP_TEST_DATA_ROOT` 隔离设置目录，未读取用户数据。

**遗留**

- Server、Agent、Desktop 进程已停止；隔离数据库、日志和设置保留在 `artifacts/test-runs/startup-20260804-a1f8e5fb06a74a0a8adcca04b3659aa0/` 供交接核对。
- 阶段 18、23、24、真实服务端 ZIP、真实绝对路径、真实 Prism ZIP、真实 HMCL 客户端、管理员服务/ACL、第二 Windows 平台和交付仍未执行，不得标记通过。

## 2026-08-04 — 启动隔离交互测试前端

**做了什么**

- 为未打包 WinUI 3 Desktop 增加 `--isolated-test` 启动开关；该开关只允许使用 `SERVITIUM_DESKTOP_TEST_DATA_ROOT` 指定的绝对隔离目录，普通启动仍不读取该变量。
- 启动新的隔离 Server 与 Desktop，未使用用户正式设置、数据库或目录。

**产物**

- `src/Servitium.Desktop/App.xaml.cs`
- `src/Servitium.Desktop/Models/DesktopSelfTestOptions.cs`
- `artifacts/test-runs/interactive-d7aa986cc884407e98e1aab0f15af486/`

**验证**

- `dotnet build tests/Servitium.Desktop.Tests/Servitium.Desktop.Tests.csproj -c Release --property:Platform=x64 --no-restore --nologo`：通过，0 警告、0 错误。
- `dotnet test tests/Servitium.Desktop.Tests/Servitium.Desktop.Tests.csproj -c Release --property:Platform=x64 --no-build --no-restore --nologo --logger "console;verbosity=minimal"`：38/38 通过，无跳过。
- `dotnet format Servitium.sln --verify-no-changes --no-restore --verbosity minimal`：通过。
- Server PID 90100 在 `http://127.0.0.1:9100` 监听，`/health/ready` 返回 200，15 个迁移完成；Desktop PID 119444 仍在运行并使用隔离设置目录。

**遗留**

- Agent 未启动，节点注册需由用户在隔离 Desktop 创建一次性令牌后另行启动；阶段 18、23、24 和四项真实样本验收仍未标记通过。

## 2026-08-04 — PB-039 响应式布局修复与实际 UI 启动检查

**做了什么**

- 根据用户真实启动截图登记 PB-039；确认 PB-025 记录的是页面功能缺口而非本次运行时布局阻断，因此不改写 PB-025 的已关闭事实。
- 审查并修正 `MainWindow.xaml`、`MainWindow.xaml.cs`、`ContractPage.cs` 的共享布局：折叠导航隐藏设置控件，展开侧栏使用滚动区域；标题栏按断点隐藏细节；页面根容器与卡片可拉伸；窄页头改为双行；密码/确认密码/选择/数值控件统一可用宽度；初始化按钮放入所有者表单并靠近状态反馈。
- 统一把状态、布尔值、语言代码、操作错误码和 Owner/Server 文案通过本地化展示模型渲染，修复 `zh--hans` 键转换导致的未知文案。
- 新增实际 UI Automation 启动脚本，检查控件边界、可见性、重叠、焦点、按钮可调用性、空初始化拒绝和稳定值泄漏，并保存 compact/normal/wide 截图。

**产物**

- `src/Servitium.Desktop/MainWindow.xaml`
- `src/Servitium.Desktop/MainWindow.xaml.cs`
- `src/Servitium.Desktop/Views/ContractPage.cs`
- `src/Servitium.Desktop/ViewModels/PageActionDispatcher.cs`
- `src/Servitium.Desktop/ViewModels/PageActionDispatcher.Actions.cs`
- `src/Servitium.Desktop/ViewModels/PageActionDispatcher.Helpers.cs`
- `src/Servitium.Desktop/Services/WindowStateService.cs`
- `src/Servitium.Contracts/Localization/{zh-Hans,en-US}.json`
- `src/Servitium.Desktop/Resources/{zh-Hans,en-US}/Resources.resw`
- `eng/Check-DesktopUi.ps1`
- `docs/records/problem-log.md`
- `docs/records/current-status.md`
- `docs/records/desktop-page-gap-audit.md`
- `docs/testing/acceptance-matrix.md`
- `docs/testing/requirements-traceability.md`

**验证**

- `dotnet build src/Servitium.Desktop/Servitium.Desktop.csproj -c Release --property:Platform=x64 --no-restore --nologo`：通过，0 警告、0 错误。
- `dotnet test tests/Servitium.Desktop.Tests/Servitium.Desktop.Tests.csproj -c Release --property:Platform=x64 --no-restore --nologo --logger "console;verbosity=minimal"`：38/38 通过，无跳过。
- `pwsh -NoProfile -File eng/Check-DesktopUi.ps1 -ServerAddress http://127.0.0.1:9100 -DataRoot artifacts/test-runs/interactive-d7aa986cc884407e98e1aab0f15af486/desktop`：通过；报告 `artifacts/test-results/desktop-ui/20260804-163934/`，当前 Windows DPI 150%，实际窗口有效宽度 560/960/1400，三张截图可读且 UIA 断言通过；空初始化按钮调用后 Server 仍保持未初始化。

**遗留**

- 当前环境没有安全地切换系统缩放的授权或第二显示配置；100%、125%、200% DPI 未运行，不能写成通过。
- 30 页逐页实际启动视觉、读屏/高对比度、完整键盘路径和人工验收仍未完成；PB-039 保持进行中，四项真实样本测试保持暂停，阶段 22/23/24 不得标记通过。

## 2026-08-04 — PB-040 事件流握手回归与合成页面检查

**做了什么**

- 在合成 Server `http://127.0.0.1:9201` 上复现登录后的事件流协商 HTTP 400 `csrf-token-missing`；确认 Server 的 `/hubs` CSRF 保护要求请求头和 Cookie 同时存在。
- 修正 `Servitium.Client` 的事件流握手：发送 `X-CSRF-Token`、会话 Cookie 与 CSRF Cookie；同时让实例和审计查询在可选 GUID 为空时省略查询参数，避免发送 `nodeId=` 或 `initiatedBy=`。
- 在 `MainWindow.xaml`、`MainWindow.xaml.cs` 与 `ContractPage.cs` 继续收敛页面可用高度、滚动和拉伸布局；本轮暂停前在 `App.xaml.cs` 增加了仅隔离启动使用的 `--ui-page-test <PG-ID>` 入口，但该最后改动尚未重新构建。

**验证**

- `dotnet test tests/Servitium.Client.ContractTests/Servitium.Client.ContractTests.csproj -c Release --no-restore --nologo --logger "console;verbosity=minimal"`：15/15 通过，无跳过；包含空/有值可选 GUID 查询回归。
- 修复后的 Desktop Release 构建在 `App.xaml.cs` 最后一次改动之前通过，0 警告、0 错误；修复后的合成 Desktop 恢复持久会话并显示“连接已就绪”。
- 合成 UIA 检查选择 8 个顶层导航页面：PG-040、PG-010、PG-020、PG-060、PG-030、PG-041、PG-044、PG-032；每页都有可见可交互控件，无模态遮挡、窗口外控件或 `False`/`zh-Hans`/`en-US` 原始值泄漏。PG-041 的滚动字段通过 `ScrollItemPattern.ScrollIntoView()` 检查。
- 最新一轮使用的合成 Server PID 77496 与 Desktop PID 132740 已停止；保留的隔离 Server PID 90100 未由本轮启动。

**遗留**

- `--ui-page-test` 最后修改未构建或运行；Desktop 38/38 与 `eng/Validate.ps1` 尚未针对本轮全部代码重跑。
- 上述 UIA 证据只覆盖 8 个顶层导航页面和当前实际 150% DPI；30 页逐页、100%/125%/200% DPI、读屏/高对比度、完整人工视觉与四项真实样本测试仍暂停。PB-039 继续阻断阶段 22。
