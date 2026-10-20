# HMCL 客户端行为规范（场景视角）

调查日期：2026-08-02；Prism 和本地原型快照为 2026-08-03。

## 问题

记录该快照中的可观察行为，作为独立实现的依据。

## 方法

下述 SOURCE/RUNTIME 等级及提交只代表原调查，不代表当前参考仓库或本产品验收。原始证据外置保存。

## 发现

证据快照：`HMCL@d297417`。本文档从玩家客户端视角记录 HMCL 的可观察行为，作为 Servitium HMCL 分发端到端测试的设计依据。

`[RUNTIME]` 表示探针实际执行观察，`[SOURCE]` 表示源码调用链确认。协议字段与服务端要求见 `../reference/protocol-hmcl_update.md`，取证和决定变更规则见 `../reference/upstream-references.md`。

编号 `HCB-###` 供 `docs/testing/` 引用。

## 1. 场景总表

| 编号 | 场景 | 验证方式 |
| --- | --- | --- |
| HCB-001 | 首次安装（远程 manifest） | `[SOURCE]` |
| HCB-002 | 普通启动，远端无变化 | `[RUNTIME]` |
| HCB-003 | 增量更新：新增文件 | `[RUNTIME]` |
| HCB-004 | 增量更新：修改文件 | `[RUNTIME]` |
| HCB-005 | 增量更新：删除文件 | `[RUNTIME]` |
| HCB-006 | 用户已修改的受管理文件 | `[RUNTIME]` |
| HCB-007 | 本地缺失的受管理文件 | `[RUNTIME]` |
| HCB-008 | 用户自有文件（不受管理） | `[RUNTIME]` |
| HCB-009 | 用户停用的模组（`.disabled` / `.old`） | `[SOURCE]` |
| HCB-010 | 加载器或游戏版本变化 | `[SOURCE]` |
| HCB-011 | 单个文件下载失败（404） | `[RUNTIME]` |
| HCB-012 | 单个文件哈希不匹配 | `[SOURCE]` |
| HCB-013 | manifest 无效或不可解析 | `[SOURCE]` |
| HCB-014 | 服务不可达 / 超时 | `[SOURCE]` |
| HCB-015 | 服务端 5xx | `[SOURCE]` |
| HCB-016 | 下载中断后恢复 | `[SOURCE]` |
| HCB-017 | 条件请求与缓存 | `[RUNTIME]` |
| HCB-018 | 缓存指令导致的静默停更 | `[RUNTIME]` |
| HCB-019 | 非 ASCII 与含空格路径 | `[RUNTIME]` |
| HCB-020 | Channel 回滚 | `[RUNTIME]`（等价于 HCB-004/005 的反向） |
| HCB-021 | 清空所有受管理文件 | `[SOURCE]` |
| HCB-022 | 令牌吊销后的已安装客户端 | `[SOURCE]`（等价于 HCB-011 的 403 变体） |

## 2. 首次安装

### HCB-001 首次安装（远程 manifest）

**用户操作**

1. 在 HMCL 中选择安装整合包 → 选择「远程」入口；
2. 粘贴 Servitium 提供的 URL（必须以 `server-manifest.json` 结尾）；
3. HMCL 下载并解析 manifest，展示名称、版本、作者，可查看描述；
4. 用户确认实例名后开始安装。

**客户端行为** `[SOURCE]`

* URL 判定：仅当字符串以 `server-manifest.json` 结尾才走 manifest 分支，否则整个 URL 被当作整合包 ZIP 下载；
* 实例名默认取 manifest 的 `name` 并 `trim()`，需通过合法性与重名校验；
* 依次执行：按 `addons` 构建游戏与加载器 → 以空的旧文件列表执行一次同步 → 全量下载 `files` 中每一条；
* 实例被标记为整合包实例并强制进入版本隔离模式，运行目录 = 版本根目录；
* 安装失败时删除实例目录；若失败发生在文件同步阶段且非「文件不存在」类错误，则保留实例，用户下次启动时会重试同步。

**Servitium 侧要求**

* 界面必须向运营者展示可直接复制的、以 `server-manifest.json` 结尾的完整 URL；
* 不得提供带查询参数的分发链接。

**期望结果**

* 实例创建成功，`files` 中所有文件落到版本根下的正确相对位置；
* 版本根下生成 `modpack.json`，其 `type` 为 `Server`。

## 3. 更新场景

### HCB-002 普通启动，远端无变化

`[RUNTIME]` 客户端在启动前发起一次 `GET {fileApi}/server-manifest.json`（带 `If-None-Match`，若上次响应含 ETag）。服务端返回 `304` 或返回与上次内容一致的 `200` 后：

* 加载器映射相等 → 不做版本操作；
* 所有 `files` 条目哈希与旧 manifest 相同且磁盘一致 → **零个** `overrides/` 请求；
* 无删除；
* `modpack.json` 被重写（内容等价）。

**期望结果**：启动流程无可见延迟，无文件变动。

### HCB-003 增量更新：新增文件

`[RUNTIME]` 新 manifest 中出现旧 manifest 没有的 `path`。

* 客户端发起 `GET {fileApi}/overrides/{path}`；
* 下载后按 SHA-1 校验；
* 写入版本根下对应位置，必要时创建中间目录。

**实测**：`mods/added.jar` 被创建，内容等于远端内容。

### HCB-004 增量更新：修改文件

`[RUNTIME]` 条件：新哈希 ≠ 旧哈希，且磁盘哈希 == 旧哈希。

* 客户端下载并覆盖。

**实测**：`mods/changed.jar` 从 v1 内容变为 v2 内容。

### HCB-005 增量更新：删除文件

`[RUNTIME]` 条件：`path` 在旧 manifest 中、不在新 manifest 中、且磁盘上存在。

* 客户端**直接删除**该文件，不做任何确认；
* 删除在下载阶段之前完成，与下载成败无关。

**实测**：`mods/removed.jar` 被删除。

**运营含义**：从 Channel 中移除一个文件会立刻从所有玩家客户端删除该文件。Servitium 的发布预览必须明确列出「将被删除的文件」。

### HCB-006 用户已修改的受管理文件

`[RUNTIME]` 条件：磁盘哈希 ≠ 旧 manifest 哈希（说明玩家改过）。

* 无论新旧哈希是否相同，客户端**都不下载**，保留玩家的本地内容；
* 该文件的旧哈希在 `modpack.json` 被重写后更新为**新 manifest 的哈希**，因此下一次更新时，若玩家不再改动，磁盘哈希仍 ≠ 新记录哈希，该文件将**永久不再被更新**。

**实测**：`mods/usermod.jar` 保留玩家内容。

**运营含义**：这是 HMCL 的既定行为，不可通过服务端改变。玩家一旦修改受管理文件，该文件即脱离管理。Servitium 文档与界面需向运营者说明这一点，并建议把「玩家应当能自定义」的文件（如客户端按键配置）排除出 manifest。

### HCB-007 本地缺失的受管理文件

`[RUNTIME]` 条件：`path` 同时在新旧 manifest 中，但磁盘上不存在（玩家删除或上次下载失败）。

* 客户端重新下载。

**实测**：`mods/missing.jar` 被补齐。

### HCB-008 用户自有文件

`[RUNTIME]` 从未出现在任何 manifest 中的文件永远不被触碰。

**实测**：`mods/notinmanifest.jar` 原样保留。

### HCB-009 用户停用的模组

`[SOURCE]` 当目标文件位于**运行目录下的 `mods/`** 中，且同目录存在 `<文件名>.disabled` 或 `<文件名>.old` 时，客户端跳过该文件的下载与覆盖。

注意：该判定使用 `运行目录/mods`，而目标路径基于 `版本根`。在整合包实例中二者相同，因此规则生效。

**Servitium 侧要求**：不得在 manifest 中登记 `*.disabled` 或 `*.old` 文件；发布前校验应拦截。

### HCB-010 加载器或游戏版本变化

`[SOURCE]` 客户端把新旧 `addons` 各自转换为 `id -> version` 映射并整体比较（键集合与值都必须相同）。不相等时：

* 构造一个游戏构建任务，按新 `addons` 逐项施加；
* `game` 决定 Minecraft 版本，其余作为加载器版本；
* 该任务与文件同步在同一次启动流程中执行，会触发从 Mojang / BMCLAPI 拉取游戏与加载器文件（这是**客户端到外部源**的流量，与 Servitium 无关）；
* 任一 `id` 无法被识别为已知安装器时，构建失败，整个启动被阻止。

**触发重装/重建的条件**：仅 `addons` 映射不相等。修改 `version`、`name`、`description` 或任何 `files` 内容都**不会**触发版本重建。

**Servitium 侧要求**：发布表单中的加载器声明只允许七个已知 id；游戏版本必填。变更加载器的 Release 在发布预览中必须以醒目方式提示「玩家将重新下载游戏与加载器」。

## 4. 失败场景

### HCB-011 单个文件下载失败（404）

`[RUNTIME]` 任一 `overrides/{path}` 返回 4xx：

* 该 URL **不重试**，立即抛 `FileNotFoundException`；
* 包装为 `DownloadException` 使整个同步任务失败；
* **游戏启动被中止**；
* HMCL 弹出错误对话框，提示整合包补全失败；
* `modpack.json` **不被重写**（写回发生在 postExecute，仅成功时执行），因此下次启动会重新尝试完整的同一批下载；
* 但**删除步骤已经执行过**——被移出新 manifest 的文件已经被删掉了。

**实测**：探针中缺失 `mods/missing.jar` 导致 `TASK_OK=false`，异常为 `DownloadException` caused by `FileNotFoundException`。

**Servitium 侧要求（关键）**：发布前必须逐条校验 manifest 中每个文件在 Release 存储中真实存在且可读，否则禁止发布。这是最高优先级的发布门禁。

### HCB-012 单个文件哈希不匹配

`[SOURCE]` 下载完成后完整性校验失败：

* 视为该次尝试失败，进入重试（最多 5 次，间隔 200 ms）；
* 全部失败后抛 `DownloadException`，后果同 HCB-011；
* 目标文件不会被写入损坏内容（下载先落临时文件，校验通过才落位）。

**Servitium 侧要求**：Release 物化后必须复核存储文件哈希与 manifest 一致；内容寻址存储天然满足，但仍需在发布前校验中显式检查。

### HCB-013 manifest 无效或不可解析

`[SOURCE]` 分两类：

* **JSON 语法错误 / `fileApi` 为 null / `files` 为 null**：抛 `JsonParseException` → 包装为 `IOException` → 同步任务失败 → 启动被中止；
* **`files` 中某条目缺 `path` 或 `hash`**：同上，整个 manifest 解析失败。

首次安装场景下，解析失败时 HMCL 直接提示 manifest 格式错误并结束向导。

**Servitium 侧要求**：manifest 由服务端物化生成，不接受用户手写；生成后必须自校验一次。

### HCB-014 服务不可达 / 超时

`[SOURCE]` 连接或读取超过 10 秒即超时，计入重试（最多 5 次）。全部失败后：

* 若失败发生在 manifest 拉取阶段：同步任务失败，启动被中止；
* 错误对话框展示为下载超时。

**Servitium 侧要求**：HMCL 端点必须在 10 秒内开始输出响应；大文件必须流式发送。

### HCB-015 服务端 5xx

`[SOURCE]` 非 2xx、非 3xx、非 4xx 的状态码抛 `ResponseCodeException`，计入重试（最多 5 次，间隔 200 ms）。重试全部失败后同 HCB-011。

`429` 与 `503` 同样计入重试。因此短暂限流是可恢复的，但持续限流会阻止玩家启动。

**Servitium 侧要求**：HMCL 端点的限流阈值必须显著高于「单客户端 64 并发 × 5 重试」，并对同一 Channel 的读取采用宽松策略。

### HCB-016 下载中断后恢复

`[SOURCE]` 客户端在同一次下载任务内支持断点续传，条件全部满足才启用：

1. 首次响应为 `200`；
2. 响应含 `Accept-Ranges: bytes`；
3. `Content-Encoding` 为 identity（**未压缩**）；
4. `Content-Length` ≥ 0；
5. 响应含强 `ETag`（非 `W/` 前缀）或 `Last-Modified`。

满足时，重试请求携带 `Range: bytes=<已收字节>-` 与 `If-Range: <强ETag 或 Last-Modified>`。服务端返回 `206` 且 `Content-Range`、`Content-Length`、`ETag` 全部自洽时才继续续传，否则从头重下。返回 `416` 时放弃续传并整重下一次。

跨进程不续传：HMCL 重启后从零开始该文件。

**Servitium 侧要求**：`overrides/` 必须支持 `Range`、返回强 ETag、且对这些响应**不启用** gzip（否则续传条件 3 不满足，弱网下每次中断都要整重下）。

### HCB-022 令牌吊销后的已安装客户端

`[SOURCE]` 令牌编码在 URL 路径中。吊销后：

* manifest 请求返回 `403` → 4xx → 立即失败 → 启动被中止；
* 玩家看到「整合包补全失败」错误，需要运营者提供新链接并重新安装或手工修改本地 `modpack.json`。

**Servitium 侧要求**：令牌轮换界面必须以破坏性操作对待，明确提示「所有已安装玩家将无法启动，必须重新安装」。

## 5. 缓存与条件请求

### HCB-017 条件请求与缓存

`[RUNTIME]` 当服务端对 `server-manifest.json` 返回 `ETag` 且 `Cache-Control: no-cache` 时：

* 首次：`GET`，无条件头，`200`；
* 后续每次启动：`GET`，携带 `If-None-Match: "<上次 ETag>"`；
* 服务端返回 `304`：客户端使用本地缓存的 manifest 内容，同步照常进行；
* 服务端返回 `200`：客户端使用新内容并更新缓存。

**实测三轮**：第 1 轮 `200`，第 2、3 轮均为 `304`，任务全部成功。

`overrides/` 请求在三轮中均**未携带**任何条件请求头。

### HCB-018 缓存指令导致的静默停更

`[RUNTIME]` 当服务端对 `server-manifest.json` 同时返回 `ETag` 与 `Cache-Control: max-age=60` 时：

* 首轮 `200` 并写入本地缓存（含过期时间）；
* 第 2、3 轮：客户端**完全不发起任何 HTTP 请求**，直接使用本地缓存 manifest；
* 因此在 `max-age` 期间发布的更新对玩家不可见，且服务端日志中看不到任何请求。

**实测**：第 2、3 轮 HTTP 请求日志为空。

**结论（强制）**：Servitium 的 `server-manifest.json` 响应必须使用 `Cache-Control: no-cache`，且**禁止**设置 `Expires` 为未来时间。此项必须有自动化测试守护（见 `../../validations/integration-backend/validation.md`）。

## 6. 路径与编码

### HCB-019 非 ASCII 与含空格路径

`[RUNTIME]` `path` 为 `中文目录/测试 文件.txt` 时，客户端请求：

```text
GET /pack/overrides/%E4%B8%AD%E6%96%87%E7%9B%AE%E5%BD%95/%E6%B5%8B%E8%AF%95%20%E6%96%87%E4%BB%B6.txt
```

即：UTF-8 百分号编码，空格为 `%20`，`/` 保持原样。文件被正确写入本地对应路径。

**Servitium 侧要求**：端点必须按 UTF-8 解码；测试用例必须包含中文目录名、中文文件名与含空格文件名。

## 7. 发布与回滚

### HCB-020 Channel 回滚

`[RUNTIME]` 等价性论证：回滚把 Channel 指向历史 Release，客户端看到的是一份「新 manifest」，其内容恰为历史状态。因此：

* 新版新增的文件 → 在回滚后的 manifest 中不存在 → **被删除**（HCB-005）；
* 新版修改过的文件 → 哈希回到旧值 → **被下载覆盖**（HCB-004）；
* 玩家自行修改过的文件 → **不受影响**（HCB-006）。

**Servitium 侧要求**：回滚必须在界面上以与发布相同的预览方式展示「将新增 / 覆盖 / 删除」的文件集合。

### HCB-021 清空所有受管理文件

`[SOURCE]` 发布一个 `files` 为空数组的 Release：客户端会删除旧 manifest 中列出的全部文件，且不下载任何内容。

**Servitium 侧要求**：允许该操作，但发布确认对话框必须以破坏性操作样式呈现并要求二次确认。

## 8. 用户可见结果

| 客户端状态 | 玩家看到的内容 |
| --- | --- |
| 同步进行中 | 启动进度界面中出现整合包下载阶段，显示待下载文件数与总体进度 |
| 同步成功 | 无额外提示，直接进入登录与启动阶段 |
| 文件不存在类失败 | 错误对话框，提示整合包内容未找到 |
| 其他同步失败 | 错误对话框，提示整合包补全出错 |
| 下载超时 | 错误对话框，提示下载超时并给出 URL |
| HTTP 404 | 错误对话框，提示 404 并给出 URL |
| 加载器构建失败 | 错误对话框，提示安装失败 |

以上均由 HMCL 自身呈现，Servitium 无法定制。因此 Servitium 的对策只能是**在服务端侧杜绝这些失败**。

## 9. 端到端测试映射

`../../validations/ui-clients/validation.md` 与 `../../validations/integration-backend/validation.md` 需覆盖下列组合：

| 测试项 | 覆盖场景 |
| --- | --- |
| 首次安装 | HCB-001 |
| 无变化启动 | HCB-002、HCB-017 |
| 新增 / 修改 / 删除 | HCB-003、HCB-004、HCB-005 |
| 玩家改动保护 | HCB-006 |
| 缺失补齐 | HCB-007 |
| 自有文件保护 | HCB-008 |
| 停用模组跳过 | HCB-009 |
| 加载器变更 | HCB-010 |
| 缺文件发布拦截 | HCB-011（负向：验证发布被拒绝） |
| 哈希损坏 | HCB-012 |
| manifest 缓存指令 | HCB-018（断言响应头为 `no-cache`） |
| Unicode 路径 | HCB-019 |
| 回滚 | HCB-020 |
| 令牌吊销 | HCB-022 |

真实 HMCL 客户端端到端验证的执行方式与前置条件见 `../../validations/acceptance-product/validation.md`。

## 结论

采用的产品决定见项目范围和对应设计；不复制参考实现表达，不将历史探针当作当前联调。
