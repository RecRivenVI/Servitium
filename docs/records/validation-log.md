# 验证日志

本文件是**可持续更新**的动态记录，记录每次实际运行的验证及其结果。

## 1. 记录格式

```text
## YYYY-MM-DD — <验证名称>

| 项 | 内容 |
| --- | --- |
| 阶段 | 阶段编号 |
| 命令 | 实际执行的命令 |
| 环境 | 操作系统版本、SDK 版本、数据目录 |
| 结果 | 通过 / 失败 |
| 耗时 | |
| 产物 | artifacts/test-results/... |

**阶段结果**
| 阶段 | 结果 | 耗时 |

**失败详情**（如有）
```

要求：命令与结果必须是实际发生的，不得填写预期值。

---

## 2026-08-02 — HMCL 服务端整合内容自动更新协议运行验证

| 项 | 内容 |
| --- | --- |
| 阶段 | 00 |
| 目的 | 用真实 HMCL 代码验证协议规范中的更新算法与 HTTP 行为，避免把结论建立在源码推测上 |
| 被测对象 | `HMCL@d297417909208deb3a4bb8378af4769f97de44c8`（`main`，`v3.17.0.351-37`，工作树干净） |
| 方法 | 直接用 `javac` 编译 `HMCLCore/src/main/java` 的 430 个源文件到会话临时目录；依赖 jar 取自本机 Gradle 缓存（通过一次性 init script 导出运行时 classpath）；在临时目录编写探针程序，启动 `com.sun.net.httpserver` 本地服务并驱动 HMCL 自身的 `ServerModpackCompletionTask` 执行 |
| 对参考仓库的影响 | 无。未修改任何源码，未新增任何文件，未在仓库内产生构建产物 |
| 产物位置 | 会话临时目录（已按净室要求不保留） |
| 结果 | 通过；全部预期行为被证实 |

### 场景与观察

构造：本地已安装 v1（旧 `modpack.json`），远端发布 v2。覆盖新增、修改、未变更、用户已改动、本地缺失、远端删除、不受管理、非 ASCII + 含空格路径共 8 类文件。

**实测 HTTP 请求（首轮，全量同步）**

```text
GET /pack/server-manifest.json                     [Accept-Encoding=gzip; User-Agent=HMCL; Accept-Language=zh-CN] -> 200
GET /pack/overrides/mods/added.jar                 [同上] -> 200
GET /pack/overrides/mods/changed.jar               [同上] -> 200
GET /pack/overrides/mods/missing.jar               [同上] -> 200
GET /pack/overrides/config/disabled.cfg            [同上] -> 200
GET /pack/overrides/%E4%B8%AD%E6%96%87%E7%9B%AE%E5%BD%95/%E6%B5%8B%E8%AF%95%20%E6%96%87%E4%BB%B6.txt  [同上] -> 200
```

最后一条对应 `path` 为 `中文目录/测试 文件.txt`。

**实测文件系统结果**

| 场景 | 结果 |
| --- | --- |
| 新增（`mods/added.jar`） | 已下载，内容等于远端值 |
| 修改（`mods/changed.jar`） | 已覆盖为 v2 内容 |
| 未变更（`mods/untouched.jar`） | 无请求，文件未变 |
| 用户已改动（`mods/usermod.jar`） | 保留用户内容，未被覆盖 |
| 本地缺失（`mods/missing.jar`） | 已补齐 |
| 远端删除（`mods/removed.jar`） | 已删除 |
| 不受管理（`mods/notinmanifest.jar`） | 原样保留 |
| 非 ASCII + 空格路径 | 已下载到正确本地路径 |
| 成功后的状态写回 | `modpack.json` 被重写为新 manifest，`overrides` 为新文件清单 |

**失败路径实测**

把 manifest 中的一个条目置为服务端不存在：`overrides/mods/missing.jar` 返回 `404` → 客户端抛 `DownloadException`（caused by `FileNotFoundException`）→ 整个同步任务失败（`TASK_OK=false`）→ 该 URL 不重试。

**缓存行为实测（三轮）**

| 服务端响应头 | 第 1 轮 | 第 2 轮 | 第 3 轮 |
| --- | --- | --- | --- |
| `ETag` + `Cache-Control: no-cache` | `200` | `GET` 带 `If-None-Match` → `304` | 同左 → `304` |
| `ETag` + `Cache-Control: max-age=60` | `200` | **零请求** | **零请求** |

第二组是关键发现：manifest 带正的 `max-age` 时，HMCL 在该时长内完全不发起请求，运营者发布的更新对玩家不可见且服务端日志中看不到任何痕迹。据此把「manifest 端点 `Cache-Control` 恒为 `no-cache`」定为强制要求（`REQ-HMC-030`、`THR-064`）。

三轮中 `overrides/` 请求均**未携带**任何条件请求头。

### 结论去向

* 协议规范：`docs/reference/hmcl-auto-update-protocol.md`（标注 `[RUNTIME]` 的条目）
* 客户端场景：`docs/reference/hmcl-client-behavior.md`（`HCB-002` … `HCB-008`、`HCB-011`、`HCB-017` … `HCB-019`）
* 需求：`REQ-HMC-030`、`REQ-HMC-031`、`REQ-HMC-042`
* 威胁：`THR-064`

### 未通过运行触发的分支

下列分支通过源码调用链确认，标注为 `[SOURCE]`，在 `hmcl-client-behavior.md` 中逐条注明：断点续传与 `If-Range`、`416` 处理、3xx 重定向跟随、超时与重试计数、加载器变更触发的版本重建、`.disabled` / `.old` 兄弟文件跳过。

---

## 2026-08-02 — 文档一致性检查

| 项 | 内容 |
| --- | --- |
| 阶段 | 00 |
| 方法 | 会话临时脚本（按净室与临时脚本规则不保留；等价逻辑将在阶段 10 迁入 `eng/`） |
| 范围 | `README.md`、`AGENTS.md`、`docs/**` |
| 结果 | 通过 |

**检查项与结果**

| 检查 | 结果 |
| --- | --- |
| Markdown 内部链接指向存在的文件 | 通过 |
| 无空文件、无仅有标题的占位文档 | 通过 |
| 无 `.gitkeep` | 通过 |
| 需求编号唯一 | 通过（262 条） |
| 被引用的需求编号均已定义 | 通过 |
| 阶段文件名与 `master-plan.md` 的阶段列表一致 | 通过（15 份） |
| `docs/` 下文件与目录命名为小写 `kebab-case` | 通过（`README.md`、`AGENTS.md` 按生态固定名例外） |
| 正式产品文档中无「待研究」「待定」「视情况」「参考 MCSManager」「参考 HMCL 实现」等模糊表述 | 通过 |
| 未创建 `src/`、`tests/`、`eng/`、`installer/`、`artifacts/`、`.github/` | 通过 |
| 追溯矩阵覆盖全部需求域 | 通过 |
| 验收矩阵覆盖三类验收 | 通过 |

---

## 2026-08-03 — 提交前文档一致性复检

| 项 | 内容 |
| --- | --- |
| 阶段 | 00 |
| 目的 | 阶段 00 基线首次提交前的完整复检；不复用 2026-08-02 的结论，全部检查重新实际执行 |
| 方法 | 会话临时目录中的 PowerShell 7 脚本（按临时脚本规则不保留；等价逻辑在阶段 10 迁入 `eng/`） |
| 范围 | `README.md`、`AGENTS.md`、`docs/**`，共 57 个文件、11 个目录 |
| 环境 | Windows 11 Pro for Workstations 10.0.28000；PowerShell 7 |
| 结果 | 通过（失败项 0，提示项 7） |

**检查项与结果**

| # | 检查 | 结果 |
| --- | --- | --- |
| 1 | 仓库仅含 Markdown 文件（57 个），无二进制、无临时文件 | 通过 |
| 2 | 无空文件、无仅有标题的文档、无内容过少（<5 行）的文档 | 通过 |
| 3 | 无空目录、无 `.gitkeep` | 通过 |
| 4 | `src/`、`tests/`、`eng/`、`installer/`、`artifacts/`、`.github/`、`node_modules/`、`bin/`、`obj/` 均不存在 | 通过 |
| 5 | `docs/` 下文件与目录为小写 `kebab-case`；`README.md`、`AGENTS.md` 按生态固定名例外 | 通过 |
| 6 | Markdown 相对链接（10 条）全部指向存在的文件；含锚点的链接标题存在 | 通过 |
| 7 | 反引号内的 `.md` 路径引用（533 条）全部可解析 | 通过 |
| 8 | 需求编号唯一（262 条定义，无重复） | 通过 |
| 9 | 被引用的需求编号（262 个）全部已定义，无未定义引用 | 通过 |
| 10 | 追溯矩阵覆盖 262/262 条需求（范围写法按编号展开后核对），无矩阵独有编号 | 通过 |
| 11 | 追溯矩阵 15 张需求表的表头与数据行均为 6 列 | 通过 |
| 12 | 验收矩阵含后端、前端、用户验收与门禁四部分；字母段 `BA-A…S`、`FA-A…P` 与 `backend-acceptance.md`、`frontend-acceptance.md` 一致 | 通过 |
| 13 | 两份矩阵中不存在「通过」「已实现未测试」「进行中」等虚假状态 | 通过 |
| 14 | 阶段文件 15 份，编号序列 `00 → 10 … 18 → 20 … 24` 与 `master-plan.md` 阶段总表一致；编号 → 文件映射一一对应 | 通过 |
| 15 | `master-plan.md` 第 5 节的 21 个固定章节在每份阶段文档中齐备且顺序一致 | 通过 |
| 16 | `AGENTS.md` 受保护清单（17 条）与动态记录清单（8 条）互不重叠，且完整覆盖全部 57 份文档 | 通过 |
| 17 | `docs/index.md` 中 `testing/` 的保护状态标注与 `AGENTS.md` 清单一致 | 通过 |
| 18 | 正式文档中无未解决占位内容（`TODO`、`TBD`、`FIXME`、「待调查」「待研究」「待定」「视情况」等；剥离引号与行内代码中的术语提及后命中 0 处） | 通过 |
| 19 | `docs/reference/` 全部文档标注证据级别；HMCL 两份含 `[RUNTIME]` 与 `[SOURCE]`，MCSManager 三份声明调查对象 `MCSM@072fc59` 且与 `source-evidence-index.md` 登记的提交一致 | 通过 |
| 20 | 参考证据均可定位到登记快照，净室边界明确，阶段文档没有把证据事实误写为产品决定 | 通过 |
| 21 | `README.md` 与 `AGENTS.md` 不陈述阶段状态、进度、日期等动态内容 | 通过 |
| 22 | `current-status.md` 概览与阶段表与实际一致（阶段 00 已完成、其余未开始、源码为无） | 通过 |
| 23 | 动态记录使用绝对日期 `YYYY-MM-DD`，无相对日期表述 | 通过 |

**提示项（不构成失败）**

| # | 提示 |
| --- | --- |
| 1 | `docs/development/phases/17-backend-hmcl-distribution.md` 含 23 个章节：21 个固定章节齐备且顺序一致，另附加「参考文档」与「协议级真实客户端验证」。`docs/development/master-plan.md` 第 5 节要求「不省略章节」，未禁止附加章节 |
| 2 | 5 处阶段名称措辞差异（阶段 11、16、17、21、22 的 `master-plan.md` 表格名称与阶段文件 H1 详略不同）；编号与文件映射一致，不产生歧义 |
| 3 | `README.md`「许可」一节称「见仓库中的许可文件」，但仓库中尚无 `LICENSE` 文件 |

### 对参考仓库的影响

无。本次检查只读取本仓库文件。复检时确认 `D:\GitHubPublic\MCSManager`（`master` @ `072fc596767d9719ca925085b91c0df56caffa24`）与 `D:\GitHubPublic\HMCL`（`main` @ `d297417909208deb3a4bb8378af4769f97de44c8`）工作树干净，与 `source-evidence-index.md` 登记的快照一致。

---

## 2026-08-03 — 文档基线全量复核后一致性检查

| 项 | 内容 |
| --- | --- |
| 阶段 | 00 |
| 目的 | 验证全量文档改写后不存在结构断裂、引用遗漏、统计漂移、缓存契约回退或 Git 越权操作 |
| 命令 | `C:\Users\RavenYin\AppData\Local\Temp\servitium-doc-audit-20260803.ps1` |
| 环境 | Windows 10.0.28000；PowerShell 7.6.4 |
| 范围 | `README.md`、`AGENTS.md`、`docs/**`，共 57 份 Markdown |
| 结果 | 通过；19/19 项通过，失败 0 |
| 耗时 | 约 1 秒 |
| 产物 | 无仓库内产物；脚本与改写前快照位于会话临时目录，复核结束后删除 |

**检查结果**

| 检查组 | 结果 |
| --- | --- |
| 文件、编码与链接 | 57/57 均为非空 Markdown；UTF-8 无 BOM、LF 行尾；无行尾空白；内部链接无断链 |
| 需求 | 262 条定义且唯一；每条有正文；所有引用已定义；追溯矩阵 262 条逐条对应，无缺失或多余 |
| 安全 | 41 个威胁与 41 行测试索引逐一对应 |
| 阶段 | 15 份阶段文档均为 1–21 节；阶段名称与总表完全一致 |
| 参考清单 | MCSManager 304 条且分类统计为采用 195、适配 46、排除 34、不适用 29；路由 27；HMCL 客户端场景 22 |
| 界面 | 页面规格 30 个且编号唯一 |
| HMCL 缓存 | 所有关联契约均要求可变 `overrides/` URL 使用 `no-cache, no-transform`；无正 `max-age` 或 `immutable` 回退 |
| 补丁式标记 | 已知的借句、保留见下、跳过参数与整工作树 `git diff` 要求均清零 |
| Git | `develop` 未产生 `HEAD` 提交；暂存区为空；57 份 Markdown 未跟踪 |

本次没有读取或修改 MCSManager、HMCL 参考仓库。

---

## 2026-08-03 — 参考证据与提交规范一致性检查

| 项 | 内容 |
| --- | --- |
| 阶段 | 00 |
| 目的 | 验证具体模型名称和封闭式参考调查措辞已清除，实地考察与净室边界跨文档一致，Git 提交规则为单行标题且类型集合完整 |
| 命令 | PowerShell 内联只读脚本：枚举 `AGENTS.md`、`README.md` 与 `docs/**/*.md`，执行严格 UTF-8 解码、BOM/行尾/行尾空白检查、内部链接解析、禁用措辞扫描、参考路径存在性检查与提交类型集合比较 |
| 环境 | Windows 10.0.28000；Windows PowerShell |
| 范围 | `AGENTS.md`、`README.md`、`docs/**`，共 57 份 Markdown |
| 结果 | 通过；问题 0 |
| 产物 | 无；检查全部在内存中完成 |

**检查结果**

| 检查组 | 结果 |
| --- | --- |
| 文件与链接 | 57/57 非空；UTF-8 无 BOM；LF 行尾；无行尾空白；内部链接全部可解析 |
| 模型中立 | 具体模型名称扫描无命中 |
| 参考考察 | 封闭式终结判断及禁止阅读、重复考察、再次推导或枚举类措辞无命中；两个绝对路径在治理、工作流与证据索引中齐备 |
| 参考文档 | 六份既有功能、行为、布局与协议文档均保留，并统一说明证据快照、按需核实、决策记录与净室边界 |
| Git 标题 | 类型集合为 `build, chore, ci, docs, feat, fix, perf, refactor, revert, style, test`；旧中文类型和正文模板无命中 |
| Git 状态 | 本轮未执行暂存、提交、推送、打标签或发布 |

提交类型集合依据 [Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/) 与 [commitlint `type-enum` 规则](https://commitlint.js.org/reference/rules.html#type-enum)核对；本仓库在该结构上增加“禁止正文、脚注与 trailer”的更严格约束。

---

## 2026-08-03 — Git scope 规则一致性检查

| 项 | 内容 |
| --- | --- |
| 阶段 | 00 |
| 目的 | 验证提交标题只有 `type(scope): 中文摘要` 一种结构，`scope` 必填、中文优先且开放扩展 |
| 命令 | PowerShell 内联只读脚本：提取 `AGENTS.md` 第 15 节，检查废弃格式、必填规则、范围词表、示例结构，并复核工程规范、开发工作流、57 份 Markdown 的编码与内部链接 |
| 环境 | Windows 10.0.28000；Windows PowerShell |
| 范围 | `AGENTS.md`、`docs/engineering/engineering-standards.md`、`docs/development/development-workflow.md` 及全库 Markdown |
| 结果 | 通过；问题 0 |
| 产物 | 无；检查全部在内存中完成 |

**检查结果**

| 检查组 | 结果 |
| --- | --- |
| 唯一格式 | 只保留 `type(scope): 中文摘要`；范围省略格式与其他标题变体均不存在 |
| 必填规则 | `AGENTS.md`、工程规范与开发工作流均明确 `scope` 必填 |
| 中文词表 | 八个优先范围词均为宏观中文类别；必要时新增的范围必须保持同等粒度 |
| 单一范围 | 明确每个标题只使用一个主要范围，多领域且无法确定主范围时拆分提交 |
| 文档质量 | 57/57 非空；UTF-8 无 BOM；LF 行尾；无行尾空白；内部链接全部可解析 |

---

## 2026-08-03 — 本地原型参考与全量文档检查

| 项 | 内容 |
| --- | --- |
| 阶段 | 00 |
| 目的 | 验证本地整合包发布原型的证据、只读边界与开发引用已登记，新文档与全库基线一致，且三个参考路径未被修改 |
| 命令 | PowerShell 内联只读脚本：严格 UTF-8/行尾/标题/围栏/链接检查，需求与追踪集合比较，阶段结构与禁用措辞扫描；另行复算本地原型证据清单哈希并执行三个 Git 工作树检查 |
| 环境 | Windows 10.0.28000；Windows PowerShell |
| 范围 | `AGENTS.md`、`README.md`、`docs/**`，共 58 份 Markdown；三个明确参考路径 |
| 结果 | 通过；文档问题 0，参考快照与 Git 状态符合登记事实 |
| 产物 | 无；检查全部在内存中完成 |

**检查结果**

| 检查组 | 结果 |
| --- | --- |
| 文档品质 | 58/58 非空；UTF-8 无 BOM；LF 行尾；无行尾空白；每份只有一个围栏外 H1；围栏成对；内部链接全部可解析 |
| 需求追踪 | 262 条需求定义与 262 条追踪记录集合完全一致，无重复、无缺失、无额外编号 |
| 阶段与参考 | 15 份阶段文档均为 21 个二级章节；`docs/reference/` 共 7 份文档 |
| 模型与旧禁令 | 具体模型名称、封闭式终结判断以及阻止后续实地考察的旧措辞无命中 |
| Git 规则 | 唯一标题结构为 `type(scope): 中文摘要`；`scope` 必填且中文优先；禁止正文；不含标题 `!` 或破坏性变更特例 |
| 本地原型 | 15 个证据文件，共 121535 字节，最晚修改时间 2026-07-30T06:33:29Z；证据清单 SHA-256 仍为 `857b6d8c5fffa0644e0c47351dbef2908a2fa97a8ee8c6322caac82fc78c2787` |
| 参考工作树 | HMCL 为 `main...origin/main`且干净；MCSManager 为 `master...origin/master`且干净；本地原型证据清单未变 |
| Servitium Git | `develop` 未产生 `HEAD`；58 份 Markdown 未跟踪；暂存区为空 |

---

## 2026-08-03 — Prism 参考与文档语言一致性检查

| 项 | 内容 |
| --- | --- |
| 阶段 | 00 |
| 目的 | 验证 Prism 原生实例导出证据、四个参考路径、专题文档结构和全库语言一致性 |
| 命令 | 内联 Python 只读检查 Markdown 编码、行尾、标题、围栏、链接、需求追踪、阶段结构、参考开篇与禁用模式；PowerShell 复核三个 Git 参考仓库、非 Git 原型证据清单及 Servitium Git 状态 |
| 环境 | Windows 10.0.28000；Windows PowerShell；Python UTF-8 模式 |
| 范围 | `AGENTS.md`、`README.md`、`docs/**`，共 59 份 Markdown；四个明确参考路径 |
| 结果 | 通过；文档问题 0，四个参考快照未变化 |
| 产物 | 无；检查全部在内存中完成 |

**检查结果**

| 检查组 | 结果 |
| --- | --- |
| 文档品质 | 59/59 非空；UTF-8 无 BOM；LF 行尾；无行尾空白；每份只有一个围栏外 H1；围栏成对；内部链接全部可解析 |
| 需求追踪 | 262 条需求定义与 262 条追踪记录集合完全一致，无重复、无缺失、无额外编号 |
| 阶段与参考 | 15 份阶段文档均为 21 个二级章节；`docs/reference/` 共 8 份文档；7 份专题文档均在开篇登记证据快照 |
| Prism 格式 | 原生实例 ZIP 与 Modrinth、CurseForge 导出已区分；`instance.cfg`、`mmc-pack.json`、游戏目录、默认排除项与信任边界均可追溯到 `PRISM@7174c26a9` |
| 语言一致性 | 具体模型名称、封闭式调查结论、阻止实地考察的旧规则、重复层级说明与对话式授权措辞无命中 |
| 参考状态 | HMCL、MCSManager 与 Prism Launcher 工作树干净且 HEAD 与登记快照一致；本地原型 15 个证据文件共 121535 字节，清单 SHA-256 为 `857b6d8c5fffa0644e0c47351dbef2908a2fa97a8ee8c6322caac82fc78c2787` |
| Git 规则 | 唯一标题结构为 `type(scope): 中文摘要`；`scope` 必填且中文优先；禁止正文；没有破坏性变更标题特例 |
| Servitium Git | `develop` 未产生 `HEAD`；59 份 Markdown 未跟踪；暂存区为空 |

---

## 2026-08-03 — 阶段 10 统一验证连续运行

| 项 | 内容 |
| --- | --- |
| 阶段 | 10 |
| 命令 | `./eng/Validate.ps1`，连续执行 3 次 |
| 环境 | Windows 10.0.28000 x64；PowerShell 7.6.4；.NET SDK 10.0.301；Node.js 22.23.1；OpenJDK 25.0.3 x64 |
| 结果 | 通过；3/3 次完成，无跳过、无随机失败 |
| 耗时 | 第 1 次约 51 秒；第 2 次约 50 秒；第 3 次约 51 秒 |
| 产物 | `artifacts/test-results/validate-20260803-052508/`、`validate-20260803-052621/`、`validate-20260803-052718/` |

**阶段结果**

| # | 阶段 | 结果 |
| --- | --- | --- |
| 1 | 环境检查 | 3 次通过 |
| 2 | 锁定依赖还原 | 3 次通过 |
| 3 | 生成代码一致性 | 3 次通过 |
| 4 | Release 构建 | 3 次通过，0 警告、0 错误 |
| 5 | 分析器 | 3 次通过 |
| 6 | 格式 | 3 次通过 |
| 9 | 单元测试 | 3 次通过，含 Java UNT-001 |
| 10 | 架构测试 | 3 次通过，7/7 |
| 11 | 集成测试 | 3 次通过，Server 1/1、Agent 2/2、Java INT-003 |
| 17 | 覆盖率门禁 | 3 次通过；Domain/Application 与整体当前均为 100% |
| 18 | 依赖漏洞 | 3 次通过；无已知漏洞 |
| 21 | 文档一致性 | 3 次通过；57 份 `docs/` Markdown、262 条需求、387 个验收项 |

本记录是阶段 10 的开发配置验证，不是阶段 18 的 `BackendGate` 验收证据。

---

## 2026-08-03 — 阶段 11 统一验证连续运行

| 项 | 内容 |
| --- | --- |
| 阶段 | 11 |
| 命令 | `./eng/Validate.ps1 -Profile Development`，连续执行 3 次 |
| 环境 | Windows 10.0.28000 x64；PowerShell 7.6.4；.NET SDK 10.0.301；Node.js 22.23.1；OpenJDK 25.0.3 x64 |
| 结果 | 通过；3/3 次完成，无跳过、无随机失败 |
| 产物 | `artifacts/test-results/validate-20260803-060512/`、`validate-20260803-060559/`、`validate-20260803-060642/` |

**阶段结果**

| 检查 | 结果 |
| --- | --- |
| 环境、锁定还原、生成一致性 | 3 次通过；60 错误码、95 双语键、30 页面、22 事件、54 Agent 指令 |
| Release 构建、分析器、格式 | 3 次通过；0 警告、0 错误、无格式差异 |
| 单元测试 | 3 次通过；Domain 30、Application 6、Java UNT-001；无跳过 |
| 架构测试 | 3 次通过；Domain 11、Application 1；无跳过 |
| 集成测试 | 3 次通过；Server 1、Agent 2、Java INT-003；无跳过 |
| 覆盖率 | 3 次通过；末次整体 88.07%、Domain 90.29%、Application 97.03% |
| 依赖漏洞与文档 | 3 次通过；无已知漏洞；57 份 `docs/` Markdown、262 条需求、387 个验收项一致 |

失败的预运行 `validate-20260803-060340/` 已登记为 PB-012；不计入连续通过序列。本记录仍不是阶段 18 `BackendGate` 验收。

---

## 2026-08-03 — 阶段 12–17 核心链路预交接验证

| 项 | 内容 |
| --- | --- |
| 阶段 | 12–17（均为部分完成） |
| 命令 | `dotnet build Servitium.sln -c Release --no-restore`；`./eng/Test.ps1 -Category Unit/Architecture/Integration -Configuration Release`；`./eng/Check-Coverage.ps1 -Configuration Release`；`./eng/Check-Dependencies.ps1`；`./eng/Generate.ps1 -Verify`；WinUI `--self-test` |
| 环境 | Windows 10.0.28000 x64；PowerShell 7.6.4；.NET SDK 10.0.301；Windows App Runtime 1.8；OpenJDK 25.0.3 x64 |
| 结果 | 当前预检通过；无跳过；完整 `Validate.ps1` 连续运行另行记录 |
| 产物 | `artifacts/test-results/{unit,architecture,integration,coverage}`；`artifacts/test-runs/winui-selftest-4796693d0a4e45c8b25ec4a8b6e1eeeb` |

**结果摘要**

| 检查 | 结果 |
| --- | --- |
| Release 构建与格式 | 15 个项目通过，0 警告、0 错误；`dotnet format --verify-no-changes` 通过；源码/测试无 `NotImplementedException` |
| 单元与架构 | Domain 30、Application 6、Java UNT-001；Domain 架构 11、Application 架构 1，全部通过 |
| 集成 | Infrastructure 18、Server 6、Agent 38、Java INT-003，全部通过且无跳过 |
| 进程纵向链路 | 独立 Server/Agent、实际 Java 子进程、ZIP 导入、EULA、字面命令、停止和外部目录保留通过 |
| 覆盖率 | 整体 73.79%；Domain/Application 均不低于 85%；没有降低阈值 |
| 依赖与生成 | 15 个项目无已知漏洞；60 错误码、95 双语键、30 页面、22 事件、54 Agent 指令一致 |
| WinUI 启动探针 | Server PID 71884、Desktop PID 110648，连接隔离 Server 后退出码 0；探针结束后进程均已停止 |

验证期间发现并修复 PB-013 至 PB-019。失败的覆盖率预运行包括 39.37% 的重复路径报告、67.82% 的真实低覆盖报告以及一次极短寿命进程竞态；它们不计为通过，修复后才得到 73.79% 结果。本记录不是阶段 18 `BackendGate`，也不包含真实样本、管理员环境、第二 Windows 平台或用户验收。

## 2026-08-03 — 阶段 12–17 交接前统一验证连续运行

| 项 | 内容 |
| --- | --- |
| 阶段 | 12–17（均为部分完成） |
| 命令 | `./eng/Validate.ps1 -Profile Development -Configuration Release`，连续执行 3 次 |
| 环境 | Windows 10.0.28000 x64；PowerShell 7.6.4；.NET SDK 10.0.301；Node.js 22.23.1；Windows App Runtime 1.8；OpenJDK 25.0.3 x64 |
| 结果 | 通过；3/3 次完成，无跳过、无随机失败 |
| 耗时 | 约 79 秒、76 秒、75 秒 |
| 产物 | `artifacts/test-results/validate-20260803-082749/`、`validate-20260803-082912/`、`validate-20260803-083033/` |

**阶段结果**

| 检查 | 结果 |
| --- | --- |
| 环境、锁定还原、生成一致性 | 3 次通过；60 错误码、95 双语键、30 页面、22 事件、54 Agent 指令 |
| Release 构建、分析器、格式 | 3 次通过；15 个项目，0 警告、0 错误、无格式差异 |
| 单元测试 | 3 次通过；Domain 30、Application 6、Java UNT-001；无跳过 |
| 架构测试 | 3 次通过；Domain 11、Application 1；无跳过 |
| 集成测试 | 3 次通过；Infrastructure 18、Server 6、Agent 38、Java INT-003；无跳过 |
| 覆盖率 | 3 次通过；整体 73.79%，Domain/Application 均满足 85% |
| 依赖漏洞与文档 | 3 次通过；15 个项目无已知漏洞；57 份文档、262 条需求、387 个验收项一致 |

这是当前可安全自动执行的 Development 验证，不是阶段 18 `BackendGate`。真实服务端 ZIP、真实绝对目录、真实 Prism ZIP、真实 HMCL 客户端、管理员环境、第二 Windows 平台和用户验收均未执行。

## 2026-08-03 — Java 列表副作用修复后交接验证

| 项 | 内容 |
| --- | --- |
| 阶段 | 13 及 12–17 回归（均不改变正式阶段状态） |
| 命令 | `dotnet build Servitium.sln -c Release --no-restore`；Server/Agent 相关集成测试；`./eng/Validate.ps1 -Profile Development -Configuration Release` 连续执行 3 次 |
| 环境 | Windows 10.0.28000 x64；PowerShell 7.6.4；.NET SDK 10.0.301；Node.js 22.23.1；Windows App Runtime 1.8；OpenJDK 25.0.3 x64 |
| 结果 | 修复 PB-020 后通过；完整验证 3/3 次完成，无跳过、无随机失败 |
| 产物 | `artifacts/test-results/validate-20260803-085335/`、`validate-20260803-085453/`、`validate-20260803-085611/` |

**结果摘要**

| 检查 | 结果 |
| --- | --- |
| Java 列表回归 | INT-305 断言 GET 发送 `ListJava`；INT-300 进程链路读取时只返回已登记项 |
| Release 构建与格式 | 15 个项目，0 警告、0 错误；格式无差异 |
| Debug WinUI | `dotnet build src/Servitium.Desktop/Servitium.Desktop.csproj -c Debug -p:Platform=x64 --no-restore` 通过；PID 64728 连接 `http://127.0.0.1:5344` 的 `--self-test` 退出码为 0；另启动可见工作台供用户操作 |
| 单元与架构 | Domain 30、Application 6、Java UNT-001；Domain 架构 11、Application 架构 1 |
| 集成 | Infrastructure 18、Server 7、Agent 38、Java INT-003；无跳过 |
| 覆盖率、依赖、文档 | 整体 74.00%，Domain/Application 满足 85%；15 个项目无已知漏洞；57 份文档、262 条需求、387 个验收项一致 |

交接环境运行时首次构建因 Server PID 47744 与 Agent PID 107708 锁定 Release DLL 而失败；停止已核对进程并保留数据后重跑通过。`validate-20260803-085018/` 因新增测试格式失败，`validate-20260803-085140/` 因问题记录误写不存在的 `REQ-JAV-*` 编号而在文档检查失败；两次均在修复后重新执行，未计入通过序列。此处仍不是阶段 18 `BackendGate` 或真实样本验收。

## 2026-08-03 — 大型归档上传限制回归

| 项 | 内容 |
| --- | --- |
| 阶段 | 12、13、17 回归（正式状态仍为部分完成） |
| 命令 | Release 构建与格式检查；Server/Agent 集成测试；`./eng/Validate.ps1 -Profile Development -Configuration Release` 连续执行 3 次 |
| 环境 | Windows 10.0.28000 x64；PowerShell 7.6.4；.NET SDK 10.0.301；Node.js 22.23.1；Windows App Runtime 1.8；OpenJDK 25.0.3 x64 |
| 结果 | PB-021 修复后通过；完整验证 3/3 次完成，无跳过、无随机失败 |
| 产物 | `artifacts/test-results/validate-20260803-162514/`、`validate-20260803-162715/`、`validate-20260803-162919/` |

**结果摘要**

| 检查 | 结果 |
| --- | --- |
| 上传策略 | SEC-166 验证三个端点的 8 GiB 文件与 16 MiB multipart 开销限制及统一表单配置；SEC-167 验证 Kestrel 413 映射为 `file-too-large` |
| 大型合成服务端 ZIP | INT-501 以 31 MiB 不压缩负载贯通真实 Server/Agent 进程上传、传输、解压与实例登记；导入文件长度断言通过 |
| 大型合成 Prism 与 HMCL ZIP | INT-1300、INT-1310 在独立随机回环 Kestrel 进程上传同级合成 Prism ZIP；预览与 Release 创建均返回 201 |
| Release 构建与格式 | 15 个项目，0 警告、0 错误；WinUI 3 x64 构建成功；无格式差异 |
| 单元、架构与集成 | Domain 30、Application 6、Java UNT-001；架构 11+1；Infrastructure 18、Server 10、Agent 38、Java INT-003；均无跳过 |
| 覆盖率、依赖、文档 | 整体 74.02%，Domain/Application 满足 85%；15 个项目无已知漏洞；57 份文档、262 条需求、387 个验收项一致 |
| 记录落盘复检 | 更新动态记录后执行 `./eng/Validate.ps1 -Profile Development -Configuration Release -Stages 3,6,21` 通过，报告为 `artifacts/test-results/validate-20260803-163403/`；隔离 Server 健康检查 200、Agent gRPC 连接为 Established |

初次编译因新测试的 CA2000 资源处置规则失败，初次格式验证因新文件编码失败，均修复后重新运行。独立进程回归曾两次因测试夹具未显式覆盖默认监听地址而超时；改用命令行参数数组指定随机回环地址后通过。`validate-20260803-162224/` 在覆盖率已通过后被外层 120 秒调用时限终止，未执行依赖和文档阶段，因此不计入完整通过序列。上述失败均未通过降低断言、跳过测试或放宽产品安全限制处理。

只读取了隔离 Server 日志中的请求长度和 Kestrel 异常；未打开、解压、转换或执行用户真实服务端 ZIP、Prism ZIP 或 HMCL 客户端数据。真实样本测试、阶段 18、管理员环境与用户验收仍未执行。

## 2026-08-04 — 正式 Desktop 扩展后的 Development 诊断验证

| 项 | 内容 |
| --- | --- |
| 阶段 | 12–22 诊断，不构成阶段门禁 |
| 命令 | `pwsh -NoProfile -File eng/Validate.ps1 -Profile Development -Configuration Release`；修复格式后运行 `pwsh -NoProfile -File eng/Validate.ps1 -Profile Development -Configuration Release -Stages 9,10,11,17,18,21` |
| 环境 | Windows 10.0.28000 x64；PowerShell 7.6.4；.NET SDK 10.0.301；OpenJDK 25.0.3 x64 |
| 结果 | 首次运行在阶段 6 因格式差异失败；执行仓库格式化并修复无法自动处理的分析器项后格式无差异。第二次诊断中单元、架构、集成测试通过，阶段 17 因整体行覆盖率 64.84% 低于 70% 失败 |
| 产物 | `artifacts/test-results/validate-20260804-045136/`、`artifacts/test-results/validate-20260804-050049/`、`artifacts/test-results/coverage/20260804-050146/` |

**已通过的实际结果**

| 检查 | 结果 |
| --- | --- |
| Release 构建、分析器 | 全解决方案通过，0 警告、0 错误 |
| 生成一致性 | 145 个 OpenAPI 路径、181 个操作、60 个错误码、637 个本地化键、30 个页面、22 个事件、54 个 Agent 命令一致 |
| 单元测试 | Domain 30、Application 7、Desktop 25；合计 62，0 跳过 |
| 架构测试 | Domain 11、Application 1；合计 12，0 跳过 |
| 集成测试 | Infrastructure 22、Server 32、Agent 120、Client 8；合计 182，另有 Java INT-003，0 跳过 |
| 覆盖率采集测试 | 全部执行通过；最终聚合整体 64.84%，门禁失败并登记 PB-027 |

带 `-Stages` 的运行仅用于失败诊断，未执行其后依赖漏洞和文档一致性阶段，不是完整验证证据。真实样本、管理员环境和第二 Windows 平台均未执行。

## 2026-08-04 — 正式契约、Desktop 与覆盖率回归

| 项 | 内容 |
| --- | --- |
| 阶段 | 12–17、20、22 代码收敛，尚不构成阶段 18/23 Gate |
| 命令 | 相关 `dotnet test`；`dotnet build Servitium.sln -c Release --no-restore --maxcpucount:1`；`eng/Generate.ps1 -Verify`；`eng/Check-Coverage.ps1 -Configuration Release`；`eng/Check-Docs.ps1` |
| 环境 | Windows 10.0.28000 x64；PowerShell 7.6.4；.NET SDK 10.0.301；OpenJDK 25.0.3 x64 |
| 结果 | 相关回归、构建、生成、覆盖率和集中更新前的文档检查通过；连续完整验证待执行 |
| 产物 | `artifacts/test-results/coverage/20260804-131514/` |

**实际结果**

| 检查 | 结果 |
| --- | --- |
| OpenAPI 与 Client | CTR-120、CTR-100、CTR-121 通过；145 路径、181 操作具有请求、成功与 Problem Details 契约，生成 Client 操作集合双向一致 |
| 通知与 Desktop | Desktop 36/36；覆盖定时/进程内暂停、取消、队列恢复、设置重载、PG-062 Prism、五步向导、文件批量、过滤导出、本地化和权限预检查 |
| 后端纵向 | Application 11/11；Agent 指令分派 63/63；Server INT-1492/INT-1493 覆盖实例、生命周期、RCON、玩家、指标、文件、配置、模组和计划端点 |
| 生命周期稳定性 | 最初暴露输入流关闭与数据库监视任务竞态；修复后崩溃/自动重启测试连续 5 轮共 10/10 通过，未使用固定等待或放宽清理断言 |
| Release 构建 | 解决方案构建通过，0 警告、0 错误；Client 13/13 |
| 覆盖率 | Domain 41、Application 11、Infrastructure 36、Server 51、Agent 187 全通过；整体 70.83%，Domain/Application 均不低于 85%，0 跳过 |
| 生成与文档 | 60 错误码、771 本地化键、30 页面、22 事件、54 Agent 命令、181 OpenAPI 操作和 2 份 Desktop 资源一致；集中更新前文档检查为 58/262/387 |

完整验证前的首次格式检查因新增文件编码、IDE 样式和动态 `TheoryData` 初始化失败，登记 PB-033；运行统一格式器并把动态枚举改为单一测试内逐值断言后，Agent 测试项目格式检查通过。该失败不计入后续完整验证通过序列。

本轮测试只使用生成的合成归档、临时目录、隔离 SQLite 与随机回环端口。未读取四个参考路径中的用户数据，未执行四项真实样本、Windows 服务/ACL、管理员环境、第二 Windows 平台或阶段 18/23/24 正式验收。

连续完整验证的前两次 `validate-20260804-134134/`、`validate-20260804-134617/` 通过；原第 3 次 `validate-20260804-135009/` 在阶段 11 因 INT-805 于 Server 宿主释放前删除 SQLite 目录失败，登记 PB-034，故前两次不构成最终连续 3 次证据。修正清理顺序后 `ServerOperationExecutorTests` 连续 5 轮共 10/10 通过，最终完整序列从修复后重新计数。

随后为 Desktop 进程自测接入隔离数据根；直接构造 `AppServices` 的纯 xUnit 尝试因 WinRT `ResourceLoader` 环境缺失失败，登记 PB-035 并改为纯路径解析回归。该失败不计入完整验证；真实 Desktop 组合根将在隔离进程启动检查中验证。

隔离数据根改动后的第 1 次 `validate-20260804-141307/` 通过；第 2 次 `validate-20260804-141753/` 在阶段 17 因 PB-036 单次 Agent 自动重启 coverage 超时失败。相同 `XPlat Code Coverage` 配置下独立 Agent 全量运行 `agent-coverage-repro/run-1`、`run-2`、`run-3` 均 127/127，通过后从零重跑最终完整序列。

## 2026-08-04 — 最终连续 Development 验证与进程启动检查

| 项 | 内容 |
| --- | --- |
| 阶段 | 12–17 后端、20 Client、22 Desktop；不构成阶段 18/23/24 Gate |
| 命令 | `pwsh -NoProfile -File eng/Validate.ps1 -Profile Development -Configuration Release`（连续三次）；`dotnet build tests/Servitium.Desktop.Tests/Servitium.Desktop.Tests.csproj -c Release --property:Platform=x64 --no-restore`；`dotnet test tests/Servitium.Desktop.Tests/Servitium.Desktop.Tests.csproj -c Release --no-build --no-restore --property:Platform=x64`；Server/Agent/Desktop 隔离进程自检 |
| 环境 | Windows 10.0.28000 x64；PowerShell 7.6.4；.NET SDK 10.0.301；Node.js 22.23.1/npm 10.9.8；OpenJDK 25.0.3 x64 |
| 结果 | 三次完整 Development 验证通过；12 个注册阶段均通过、无跳过；Desktop 进程自检退出码 0 |
| 产物 | `artifacts/test-results/validate-20260804-142612/`、`validate-20260804-143007/`、`validate-20260804-143405/`；`artifacts/test-runs/startup-20260804-a1f8e5fb06a74a0a8adcca04b3659aa0/` |

**阶段与测试结果**

| 检查 | 实际结果 |
| --- | --- |
| 最终阶段 17 覆盖率 | Domain 41、Application 11、Infrastructure 36、Server 51、Agent 127；整体 70.85%；Domain/Application 均满足 85% |
| Desktop 单元回归 | 38/38，通过 0，跳过 0；含未打包 JSON 本地化回退与 Desktop 自测数据根测试 |
| Agent 进程自检 | `Servitium.Agent.exe --environment Development --self-test`，PID 85840，退出码 0 |
| Server 启动检查 | 显式 `--urls http://127.0.0.1:1253`，PID 72924；15 个迁移完成；`/health/ready` HTTP 200；未使用真实数据 |
| Desktop 连接检查 | `Servitium.Desktop.exe --server http://127.0.0.1:1253 --self-test`，PID 118520，退出码 0；读取隔离 Server 认证状态 |
| CSRF 负向 | 初始化请求未带 CSRF 实际返回 HTTP 400、稳定码 `csrf-token-missing`；随后同一 Cookie/请求头流程创建合成所有者 |

**边界**

- PB-037、PB-038 的第一次失败均保留在问题记录中，修复后自检和回归通过；没有把失败尝试计入通过。
- Server、Agent、Desktop 均已停止；未执行阶段 18、23、24、管理员服务/ACL、第二 Windows 平台或四项真实样本测试。

## 2026-08-04 — PB-036 重现后的最终连续序列

| 项 | 内容 |
| --- | --- |
| 阶段 | 1、2、3、4、5、6、9、10、11、17、18、21；Development，不构成阶段 18/23/24 Gate |
| 命令 | 清理已识别的 MSBuild 节点后，连续运行 `pwsh -NoProfile -File eng/Validate.ps1 -Profile Development -Configuration Release` 三次 |
| 结果 | `validate-20260804-151435/`、`validate-20260804-151831/`、`validate-20260804-152227/` 全部通过；每次 12 个阶段均通过、无跳过；覆盖率分别为 70.88%、70.85%、70.85% |
| 中间失败 | `validate-20260804-151029/` 阶段 17 的 Agent 自动重启等待超时，126/127 通过；按 PB-036 规则未计入连续序列，未修改测试截止或断言 |
| 产物 | `artifacts/test-results/validate-20260804-151029/`、`151435/`、`151831/`、`152227/` |

三次通过均实际运行依赖漏洞扫描与文档一致性检查；最终阶段 17 每轮 Domain 41、Application 11、Infrastructure 36、Server 51、Agent 127，0 跳过。四项真实样本、管理员环境、第二 Windows 平台、阶段 18/23/24 和交付仍未执行。

记录落盘后的第一次组合复核曾执行 `Generate.ps1 -Verify; Check-Docs.ps1; dotnet format ... --verify-no-changes`：`Check-Docs` 实际通过，但 `Generate` 因隔离 Server PID 72924 锁定 apphost 失败，格式检查同时报告 `LocalizationService.cs` 缺少 BOM；该次不计入通过。停止隔离 Server、由 `dotnet format` 修复编码后，三项命令重新独立执行均通过。

---

## 2026-08-04 — 隔离交互测试前端启动检查

| 项 | 内容 |
| --- | --- |
| 阶段 | 阶段 12–22，交互测试环境准备 |
| 命令 | `dotnet build tests/Servitium.Desktop.Tests/Servitium.Desktop.Tests.csproj -c Release --property:Platform=x64 --no-restore --nologo`；`dotnet test tests/Servitium.Desktop.Tests/Servitium.Desktop.Tests.csproj -c Release --property:Platform=x64 --no-build --no-restore --nologo --logger "console;verbosity=minimal"`；`dotnet format Servitium.sln --verify-no-changes --no-restore --verbosity minimal`；启动 `Servitium.Server.exe --environment Development --urls http://127.0.0.1:9100` 与 `Servitium.Desktop.exe --server http://127.0.0.1:9100 --isolated-test` |
| 环境 | Windows；Release x64；Server 数据目录 `artifacts/test-runs/interactive-d7aa986cc884407e98e1aab0f15af486/server`；Desktop 数据目录 `artifacts/test-runs/interactive-d7aa986cc884407e98e1aab0f15af486/desktop` |
| 结果 | 通过；Desktop 测试 38/38，无跳过；Server `/health/ready` 返回 200；Server PID 90100、Desktop PID 119444 保持运行 |
| 产物 | `artifacts/test-runs/interactive-d7aa986cc884407e98e1aab0f15af486/` |

**失败详情**

- 无。

## 2026-08-04 — PB-039 修复后的实际 WinUI UIA 检查

| 项 | 内容 |
| --- | --- |
| 阶段 | 阶段 22 Desktop 响应式布局与本地化回归；不构成阶段 22、23、24 Gate |
| 命令 | `dotnet build src/Servitium.Desktop/Servitium.Desktop.csproj -c Release --property:Platform=x64 --no-restore --nologo`；`dotnet test tests/Servitium.Desktop.Tests/Servitium.Desktop.Tests.csproj -c Release --property:Platform=x64 --no-restore --nologo --logger "console;verbosity=minimal"`；`pwsh -NoProfile -File eng/Check-DesktopUi.ps1 -ServerAddress http://127.0.0.1:9100 -DataRoot artifacts/test-runs/interactive-d7aa986cc884407e98e1aab0f15af486/desktop` |
| 环境 | Windows x64；当前实际窗口 DPI 150%；合成隔离 Server `http://127.0.0.1:9100`；Server/ Desktop 数据均位于 `artifacts/test-runs/interactive-d7aa986cc884407e98e1aab0f15af486/` |
| 结果 | Desktop Release 构建 0 警告/0 错误；Desktop 38/38 无跳过；UIA 实际启动 compact 560、normal 960、wide 1400 有效宽度，通过控件边界、可见性、重叠、焦点、可调用性、稳定值禁显和空初始化拒绝；最新报告 `artifacts/test-results/desktop-ui/20260804-163934/` |
| 截图 | `compact.png`、`normal.png`、`wide.png` 已保存于上述报告目录；截图由 Win32 `CopyFromScreen` 在窗口置前后采集，人工核对当前 150% DPI 的导航、表单宽度、命令层级和本地化文案 |
| 边界 | 未修改系统显示缩放，100%、125%、200% DPI 未执行；未读取真实 ZIP、真实目录、真实 Prism 包或 HMCL 客户端数据；四项真实样本、阶段 18/23/24 和完整 30 页人工视觉验收仍未通过 |

## 2026-08-04 — PB-040 修复后的客户端与合成 Desktop UIA 检查

| 项 | 内容 |
| --- | --- |
| 阶段 | 阶段 20、22；不构成阶段 22、23、24 Gate |
| 命令 | `dotnet test tests/Servitium.Client.ContractTests/Servitium.Client.ContractTests.csproj -c Release --no-restore --nologo --logger "console;verbosity=minimal"`；修复后 Desktop Release 构建；合成 Server `http://127.0.0.1:9201` 上的 Desktop UIA 启动与导航检查 |
| 结果 | Client 15/15 通过，无跳过；修复后的 Desktop 构建（`App.xaml.cs` 最后页面测试开关改动之前）0 警告/0 错误；合成会话恢复后显示“连接已就绪” |
| UIA 结果 | PG-040、PG-010、PG-020、PG-060、PG-030、PG-041、PG-044、PG-032 共 8 个顶层导航页面均有可见可交互控件；无模态遮挡、窗口外控件或 `False`/`zh-Hans`/`en-US` 原始值泄漏；PG-041 的长页面字段通过滚动进入视口 |
| DPI | 当前实际 Windows 150%（Dpi=144）；100%、125%、200% 未运行 |
| 产物 | `artifacts/test-results/desktop-ui/pg041-scrollintoview.png`；合成数据根目录 `artifacts/test-runs/desktop-pages-20260804/` |
| 边界 | 未读取真实 ZIP、真实绝对路径、真实 Prism 包、HMCL 客户端或正式数据；PB-039 仍开放，30 页逐页视觉、最新 `--ui-page-test` 改动的构建与完整 `eng/Validate.ps1` 尚未执行；四项真实样本测试保持暂停 |

## 后续格式示例

阶段 10 起，每次运行 `eng/Validate.ps1` 按下列格式记录：

```text
## YYYY-MM-DD — eng/Validate.ps1 全量

| 项 | 内容 |
| --- | --- |
| 阶段 | <编号> |
| 命令 | ./eng/Validate.ps1 |
| 环境 | Windows <版本>；.NET <版本>；Node <版本> |
| 结果 | 通过 / 失败 |
| 耗时 | |
| 产物 | artifacts/test-results/validate-<时间戳>/ |

**阶段结果**

| # | 阶段 | 结果 | 耗时 |
| 1 | 环境检查 | 通过 | |
| ... | | | |
```

失败时必须附完整错误输出与失败测试编号，并在 `problem-log.md` 中登记对应问题。
