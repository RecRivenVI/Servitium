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
