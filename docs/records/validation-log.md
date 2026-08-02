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
