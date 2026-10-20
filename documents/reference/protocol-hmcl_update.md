# HMCL 服务端整合内容自动更新协议规范

证据快照：`HMCL@d297417`。本文档是 Servitium 服务端实现 HMCL 兼容分发能力的权威协议规范，条目按运行验证 `[RUNTIME]` 或源码验证 `[SOURCE]` 标注。

未覆盖的边界采用第 12 节的安全默认策略。取证、版本复核与决定变更规则见 `../reference/upstream-references.md` 和 `AGENTS.md`。

## 1. 协议概览

HMCL 将该能力称为 `Server` 类型整合包（`ServerModpackProvider.getName()` 返回 `"Server"`）。协议由两部分组成：

1. **manifest 文档**：固定名 `server-manifest.json`，描述整合内容的元数据、文件清单与加载器需求；
2. **文件仓库**：以 `fileApi` 为根、`overrides/` 为前缀的静态文件空间。

客户端有两条入口：

* **首次安装（远程）**：用户在 HMCL 中粘贴一个以 `server-manifest.json` 结尾的 URL，客户端下载 manifest 并据此创建实例；
* **后续更新（自动）**：每次启动该实例前，客户端自动重新拉取 `server-manifest.json` 并执行差异同步。

Servitium 需要同时满足这两条入口。

## 2. Manifest 获取

| 项目 | 规范 |
| --- | --- |
| 文件名 | `server-manifest.json`（固定，不可配置） |
| 首次安装 URL | 由用户直接粘贴；HMCL 仅当 URL 字符串以 `server-manifest.json` 结尾时才按 manifest 处理，否则按整合包 ZIP 处理 |
| 更新期 URL | `{fileApi}/server-manifest.json`，其中 `fileApi` 取自**本地已保存的**旧 manifest |
| 方法 | `GET` |
| 编码 | 响应体按 `Content-Type` 的 `charset` 解码；缺省 UTF-8 |
| 期望 `Content-Type` | 任意；建议 `application/json; charset=utf-8` |
| 解析器 | Gson，未知字段静默忽略；缺失字段取该类型零值 |

`[RUNTIME]` 实测请求：

```text
GET /pack/server-manifest.json
Accept-Encoding: gzip
User-Agent: HMCL
Accept-Language: zh-CN
```

`[SOURCE]` `HMCL@d297417 : HMCLCore/.../modpack/server/ServerModpackCompletionTask.java :: preExecute()` 拼接方式为字符串直接相加 `fileApi + "/server-manifest.json"`。因此 `fileApi` **不得**以 `/` 结尾（HMCL 自身导出时会剥离尾部 `/`，见 `ServerModpackExportTask`）。Servitium 生成的 `fileApi` 必须同样无尾部斜杠。

`[SOURCE]` 首次安装入口位于 `HMCL@d297417 : HMCL/.../ui/download/ModpackSelectionPage.java :: onChooseRemoteFile()`：判定条件是 `url.endsWith("server-manifest.json")`。因此 Servitium 提供给用户复制的 URL **必须**以该文件名结尾，不得使用查询参数形式或重命名。

## 3. Manifest 结构

### 3.1 根对象

```json
{
  "name": "string",
  "author": "string",
  "version": "string",
  "description": "string",
  "fileApi": "string",
  "files": [ { "path": "string", "hash": "string" } ],
  "addons": [ { "id": "string", "version": "string" } ]
}
```

字段规范：

| 字段 | 类型 | 必填 | 语义与约束 |
| --- | --- | --- | --- |
| `name` | string | 逻辑必填 | 整合内容显示名。首次安装时作为默认实例名（`trim()` 后填入），需通过 HMCL 的实例名合法性校验。 |
| `author` | string | 否 | 作者显示名。 |
| `version` | string | 否 | 整合内容版本号，纯展示用途，**不参与**任何更新判定。 |
| `description` | string | 否 | 描述。HMCL 以内嵌 WebView 渲染，允许 HTML。 |
| `fileApi` | string | **强制** | 文件仓库根 URL。为 `null` 时 manifest 解析直接抛 `JsonParseException`。无尾部斜杠。 |
| `files` | array | **强制** | 受管理文件清单。为 `null` 时解析直接失败；允许为空数组。 |
| `addons` | array | 逻辑必填 | 加载器与游戏版本清单。必须包含 `id == "game"` 的一项，否则首次安装时抛 `Cannot find game version`。 |

`[SOURCE]` 校验逻辑：`HMCLCore/.../modpack/server/ServerModpackManifest.java :: validate()` 仅校验 `fileApi != null` 与 `files != null`；`toModpack()` 要求 `addons` 中存在 `game`。

**未知字段**：Gson 默认忽略，可安全扩展；但 Servitium 不得依赖客户端读取任何扩展字段。

**版本兼容**：manifest 无版本号字段。协议演进只能通过新增可忽略字段实现。Servitium 固定生成上述七个字段，不生成额外字段。

**解析失败行为**：`[SOURCE]` 更新期解析失败会被包装为 `IOException` 并使整个更新任务失败，进而阻止实例启动（见 `../research/investigation-hmcl_client.md` 第 6 节）。

### 3.2 `files[]` 条目

```json
{ "path": "mods/example.jar", "hash": "0e7a0276c4a4d99a771c8a4d60e910b3baf6d183" }
```

| 字段 | 规范 |
| --- | --- |
| `path` | 相对路径，**相对于客户端该实例的版本根目录**（见 3.3）。分隔符固定为 `/`。`null` 时解析失败。 |
| `hash` | **SHA-1**，小写十六进制，40 字符。`null` 时解析失败。 |
| `downloadURL` | 数据结构中存在该字段，但 `Server` 类型的更新与安装流程**完全不读取**它。Servitium 不生成该字段。 |

`[SOURCE]` `HMCLCore/.../modpack/ModpackConfiguration.java :: FileInformation.validate()` 要求 `path` 与 `hash` 均非 `null`。`ServerModpackCompletionTask` 与 `ServerModpackExportTask` 仅使用 `path` 与 `hash`。

哈希算法固定为 `"SHA-1"`，由 `ServerModpackCompletionTask` 与 `FileDownloadTask.IntegrityCheck` 硬编码。大小写：HMCL 使用 `DigestUtils.digestToString` 生成小写十六进制，比较使用 `Objects.equals`（区分大小写）。**Servitium 必须输出小写十六进制**，否则一切「未修改」判定都会失效并导致每次启动全量重下。

### 3.3 `path` 的基准目录

这是本协议最容易实现错误的一点。

`[SOURCE]` `ServerModpackCompletionTask.execute()` 中：

```text
rootPath = repository.getVersionRoot(version)          // <游戏目录>/versions/<实例名>
actualPath = rootPath.resolve(file.getPath())
```

而 `ServerModpackExportTask` 导出时，`path` 是相对 `repository.getRunDirectory(versionId)` 计算的。

两者在 HMCL 中一致，因为：`HMCL@d297417 : HMCL/.../game/HMCLGameRepository.java :: getRunDirectory()` 对被标记为整合包的实例直接返回 `getVersionRoot(id)`；且 `ModpackHelper.getInstallTask()` 在安装成功后为该实例强制加入 `PROPERTY_RUNNING_DIRECTORY` 覆盖项，使其进入版本隔离模式。

**结论**：`path` 的基准目录是客户端该实例的**版本隔离根目录**，等价于 Minecraft 的运行工作目录（放置 `mods/`、`config/`、`options.txt` 的地方）。Servitium 生成 manifest 时，`path` 必须相对于服务端整合内容的根目录，且该根目录语义上等同于「玩家客户端的 `.minecraft/versions/<实例>/`」。

### 3.4 路径规则

| 规则 | 规范 | 依据 |
| --- | --- | --- |
| 分隔符 | 仅 `/` | `[SOURCE]` 导出时 `replace(File.separatorChar, '/')` |
| 前导斜杠 | 禁止。`path` 为相对路径 | `[SOURCE]` `rootPath.resolve(path)` 语义 |
| `..` 与逃逸 | 客户端有防护：`actualPath.normalize()` 后若不再位于 `rootPath` 之下，抛 `Unsecure path`，整个更新失败 | `[SOURCE]` `ServerModpackCompletionTask.execute()` |
| 大小写 | 协议区分大小写；客户端在 Windows 上落到不区分大小写的文件系统。两个仅大小写不同的 `path` 在 Windows 客户端会互相覆盖 | `[SOURCE]` 无归一化逻辑 |
| 重复 `path` | 新 manifest 中重复 `path` 会被重复下载；旧 manifest 侧使用 `Collectors.toMap`，重复键会抛 `IllegalStateException` 并使更新失败 | `[SOURCE]` `Collectors.toMap(FileInformation::getPath, identity())` |
| 非 ASCII | 支持。客户端按 UTF-8 百分号编码后请求 | `[RUNTIME]` 见 4.2 |
| 空格 | 支持，编码为 `%20` | `[RUNTIME]` |
| 空 `path` | 会解析为版本根目录本身，属于非法输入；客户端行为未定义 | — |
| 目录条目 | 不存在。`files` 只登记常规文件；目录由客户端按需创建 | `[SOURCE]` 导出时 `Files.isRegularFile(file)` 过滤 |

Servitium 生成 manifest 时必须自行保证：路径规范化、无 `..`、无前导 `/`、无重复项（按不区分大小写比较也不得重复）、无目录条目。

### 3.5 `addons[]` 条目

```json
{ "id": "game", "version": "1.20.1" }
```

`id` 取自 HMCL 的 patch id 体系。`[SOURCE]` `HMCLCore/.../download/LibraryAnalyzer.java :: LibraryType`：

| `id` | 含义 | 导出时是否可能产生 |
| --- | --- | --- |
| `game` | Minecraft 版本，**必须存在** | 是 |
| `forge` | Forge 加载器版本 | 是 |
| `neoforge` | NeoForge 加载器版本 | 是 |
| `fabric` | Fabric Loader 版本 | 是 |
| `quilt` | Quilt Loader 版本 | 是 |
| `liteloader` | LiteLoader 版本 | 是 |
| `optifine` | OptiFine 版本 | 是 |
| `legacyfabric` | Legacy Fabric Loader | 否（`ServerModpackExportTask` 未导出，但客户端可消费） |
| `cleanroom` | Cleanroom 加载器 | 否（同上） |
| `fabric-api` / `quilt-api` / `legacyfabric-api` | 附加 API 库版本 | 否 |

`[SOURCE]` `ServerModpackExportTask.execute()` 实际导出的集合固定为：`game`、`forge`、`neoforge`、`liteloader`、`optifine`、`fabric`、`quilt`。

**未知 `id` 的行为**：`addons` 会被逐条送入 `GameBuilder.version(id, version)`。`[SOURCE]` `GameBuilder.version()` 对非 `game` 的 id 一律放入 `toolVersions`，随后由 `DefaultGameBuilder` 尝试解析为已知安装器；无法识别的 id 会导致构建任务失败，从而使首次安装或加载器变更流程失败。**Servitium 只允许生成上表中「是」的七个 id。**

## 4. 文件下载

### 4.1 URL 构造

```text
{fileApi} + "/overrides/" + {path}
```

`[SOURCE]` `ServerModpackCompletionTask.execute()` 中为字符串直接拼接：`remoteManifest.getFileApi() + "/overrides/" + file.getPath()`。

注意此处使用的是**新拉取到的远端 manifest 中的 `fileApi`**，而不是本地旧 manifest 中的 `fileApi`。因此服务端可以通过在新 manifest 中改写 `fileApi` 来迁移文件仓库地址；但 `server-manifest.json` 本身的地址仍由本地旧 manifest 决定，无法通过这种方式迁移。

`[RUNTIME]` 实测 URL：

```text
GET /pack/overrides/mods/added.jar
GET /pack/overrides/config/disabled.cfg
GET /pack/overrides/%E4%B8%AD%E6%96%87%E7%9B%AE%E5%BD%95/%E6%B5%8B%E8%AF%95%20%E6%96%87%E4%BB%B6.txt
```

第三条对应 `path` 为 `中文目录/测试 文件.txt`。

### 4.2 URL 编码

`[RUNTIME]` 客户端对拼接后的 URL 字符串按 UTF-8 百分号编码非 ASCII 字符与空格，路径分隔符 `/` 保持原样。

`[SOURCE]` 编码由 `NetworkUtils.toURI` / `WebURL.parse` 完成。

**服务端要求**：Servitium 的 HMCL 内容端点必须按 UTF-8 解码百分号编码的路径段，并且必须能正确处理 `%20`、多字节 UTF-8 序列。不得依赖 IIS/Kestrel 默认的路径归一化把 `+` 当空格（HMCL 不使用 `+` 编码空格）。

### 4.3 完整性校验

`[SOURCE]` 每个文件下载任务都带 `IntegrityCheck("SHA-1", file.getHash())`。下载完成后校验不通过则该次下载视为失败并进入重试；重试耗尽后抛 `DownloadException`，整个更新任务失败。

`[SOURCE]` 下载写入采用临时文件 + 校验通过后落位，`FileDownloadTask.getContext()` 先 `Files.createTempFile` 再原子移动。因此校验失败不会污染目标文件。

### 4.4 无 `overrides/` 之外的约定路径

协议不使用 ZIP、不使用索引文件、不使用分块清单。除 `server-manifest.json` 与 `overrides/**` 外，客户端不请求任何其他资源。

## 5. 更新算法（权威版本）

以下步骤按 `[SOURCE]` `ServerModpackCompletionTask` 的实际执行顺序描述，并已由 `[RUNTIME]` 完整验证。

**输入**

* `L`：本地 `<版本根>/modpack.json` 中的 `manifest` 对象（旧 manifest），及其 `files` 列表；
* `R`：从 `{L.fileApi}/server-manifest.json` 拉取的新 manifest；
* `rootPath`：`<游戏目录>/versions/<实例名>`，即版本隔离根；
* `modsDirectory`：`<运行目录>/mods`（整合包实例下等于 `rootPath/mods`）。

**步骤**

1. **是否启用**：若 `<版本根>/modpack.json` 不存在、或读取失败、或其 `type` 无法映射到已注册的整合包 provider，则不执行任何更新，直接放行启动。若存在但 `manifest.fileApi` 为空白字符串，同样跳过。
2. **拉取 manifest**：`GET {L.fileApi}/server-manifest.json`，解析为 `R`。失败则整个更新失败。
3. **加载器比对**：把 `L.addons` 与 `R.addons` 各自转换为 `id -> version` 映射并整体比较。若**不相等**，构造一个游戏构建任务：以实例名为目标，依次施加 `R.addons` 中的每一项（`game` 设为游戏版本，其余作为加载器版本），加入依赖任务队列。相等则不做任何版本操作。
4. **建立索引**：`Lmap = L.files` 按 `path` 索引；`Rset = R.files` 的 `path` 集合。
5. **逐条处理 `R.files` 中的每个条目 `f`**，令 `p = rootPath.resolve(f.path).normalize()`：
   1. 若 `p` 不在 `rootPath` 之下 → 抛 `Unsecure path`，整个更新失败。
   2. 若 `p` 的父目录等于 `modsDirectory`，且同目录下存在 `<文件名>.disabled` 或 `<文件名>.old` → **跳过**（用户已停用该模组，不下载、不覆盖）。
   3. 否则若 `f.path` 不在 `Lmap` 中 → **下载**（新增文件）。
   4. 否则若 `p` 在磁盘上不存在 → **下载**（补齐缺失文件）。
   5. 否则计算磁盘文件的 SHA-1 记为 `h`，取 `Lmap[f.path].hash` 记为 `oldHash`：
      * 当且仅当 `oldHash != f.hash` **且** `oldHash == h` 时 → **下载**；
      * 其余情况 → **跳过**。
        这一条同时实现了两件事：远端未变更时不重下；用户本地改动过的文件（`h != oldHash`）永远不被覆盖。
6. **删除**：遍历 `L.files`，若某条目的 `path` 在磁盘上存在、且**不在** `Rset` 中 → 删除该文件。删除是即时执行的，不受下载是否成功影响。
7. **执行下载**：所有标记为「下载」的条目作为依赖任务并发执行，每个任务带 SHA-1 完整性校验。
8. **写回状态**：仅当上述全部成功时，把 `<版本根>/modpack.json` 重写为新的 `ModpackConfiguration`，其 `manifest` = `R`，`type` = 旧值（`"Server"`），`name` = 旧值，`version` = 旧值，`overrides` = `R.files`。

**明确不做的事**

* 不删除任何从未出现在 `L.files` 中的文件（用户自有文件安全）；
* 不删除空目录；
* 不清理 `saves/`、`options.txt` 等；
* 不校验已存在且判定为「跳过」的文件的哈希；
* 不使用 `downloadURL` 字段；
* 不发送 `HEAD` 请求；
* 不做并发数协商。

### 5.1 运行验证结果

`[RUNTIME]` 场景与结果（探针实测，见 `../reference/upstream-references.md` 2.2）：

| 场景 | 旧 manifest | 新 manifest | 磁盘初值 | 期望 | 实测结果 |
| --- | --- | --- | --- | --- | --- |
| 新增 | 无 | 有 | 不存在 | 下载 | 已下载，内容为远端值 ✔ |
| 内容变更 | 有 | 有（哈希不同） | 与旧哈希一致 | 下载覆盖 | 已覆盖为新内容 ✔ |
| 未变更 | 有 | 有（哈希相同） | 一致 | 跳过 | 无请求，文件不变 ✔ |
| 用户已改动 | 有（哈希 A） | 有（哈希 C） | 哈希 B（≠A） | 跳过 | 保留用户内容 ✔ |
| 本地缺失 | 有 | 有 | 不存在 | 下载 | 已补齐 ✔ |
| 远端删除 | 有 | 无 | 存在 | 删除 | 已删除 ✔ |
| 不受管理 | 无 | 无 | 存在 | 保留 | 保留 ✔ |
| 非 ASCII + 空格路径 | 无 | 有 | 不存在 | 下载 | 已下载到正确路径 ✔ |
| 清单文件 404 | 有 | 有 | — | 整体失败 | 抛 `DownloadException`，更新任务失败 ✔ |

## 6. HTTP 行为分级

必须严格区分三类。

### 6.1 客户端**必需**的服务端行为

| 能力 | 说明 | 依据 |
| --- | --- | --- |
| `GET /…/server-manifest.json` 返回 200 与 JSON 正文 | 更新与安装的唯一入口 | `[RUNTIME]` |
| `GET /…/overrides/{path}` 对 `files` 中**每一条**返回 200 | 任一条 4xx 即导致整个更新失败并阻止启动 | `[RUNTIME]` |
| 正确处理 UTF-8 百分号编码路径 | 非 ASCII 与空格路径必需 | `[RUNTIME]` |
| 响应体字节与 manifest 中 SHA-1 一致 | 校验不通过即失败 | `[SOURCE]` |
| 若发送 `Content-Length`，其值必须与实际正文长度一致 | 不一致抛 `Unexpected file size` | `[SOURCE]` `FetchTask.download()` |
| 若发送 `Content-Encoding: gzip`，正文必须是合法 gzip | 客户端固定发送 `Accept-Encoding: gzip` | `[SOURCE]` |

### 6.2 客户端**支持但非必需**的服务端行为

| 能力 | 客户端实际行为 | 依据 |
| --- | --- | --- |
| `ETag` + `If-None-Match` | 客户端为 `server-manifest.json` 缓存 ETag 并在后续请求携带 `If-None-Match`；服务端返回 `304` 时使用本地缓存副本 | `[RUNTIME]` |
| `Cache-Control: max-age=N` / `Expires` | **有副作用**：与 ETag 同时出现时，客户端在 N 秒内**完全不发起请求**，直接使用缓存 manifest | `[RUNTIME]` |
| `Cache-Control: no-store` | 客户端不缓存该响应 | `[SOURCE]` `CacheRepository.cacheData()` |
| `Last-Modified` | 仅用于断点续传的 `If-Range` 回退；**不会**产生 `If-Modified-Since` 请求（该逻辑在源码中被注释掉） | `[SOURCE]` `CacheRepository.injectConnection()` |
| `Accept-Ranges: bytes` + `Range` + `206` | 仅在一次已开始的下载中途失败并重试时使用；正常流程不发送 `Range` | `[SOURCE]` `FetchTask.HttpResumeContext` |
| `416` | 客户端放弃续传，从头重下一次（额外增加一次重试额度） | `[SOURCE]` |
| 3xx 重定向 | 客户端手动跟随 `300`–`308`（排除 `304`、`306`），上限 20 跳，逐跳解析 `Location` | `[SOURCE]` `FetchTask.downloadHttp()` |
| gzip 传输编码 | 客户端始终发送 `Accept-Encoding: gzip` 并可解压 | `[RUNTIME]` |

**关键结论**：文件下载请求**不携带** `If-None-Match`（`FileDownloadTask` 在带完整性校验且启用缓存时走 `NOT_CHECK_E_TAG` 分支；`ServerModpackCompletionTask` 创建的任务未启用 caching，因此走 `CHECK_E_TAG`，但这些 URL 从未被写入 ETag 索引，故索引查不到、不注入请求头）。`[RUNTIME]` 三轮实测中 `overrides/` 请求均无 `If-None-Match`。

### 6.3 Servitium 为可靠性额外提供、客户端不要求的行为

* `HEAD` 支持：客户端从不发送，但便于运维与监控；
* `Range` / `206`：客户端仅在续传时使用，Servitium 仍应完整支持以提升弱网可靠性；
* 强 `ETag`（引号包裹、非 `W/` 前缀）：续传校验要求强 ETag，弱 ETag 会使续传降级为整重下；
* 稳定的 `Content-Type`；
* 访问统计与限流。

### 6.4 客户端固定发送的请求头

`[RUNTIME]` 每个请求均包含：

| 头 | 值 |
| --- | --- |
| `Accept-Encoding` | `gzip` |
| `User-Agent` | 系统属性 `http.agent`，默认 `HMCL` |
| `Accept-Language` | 客户端 locale 的 BCP 47 标签，例如 `zh-CN` |

客户端**不发送** `Authorization`、`Cookie` 或任何自定义头。因此 HMCL 分发端点**不能**依赖请求头做鉴权；如需限制访问，只能把凭据编码进 URL 路径（见第 9 节）。

### 6.5 超时与重试

`[SOURCE]`：

| 项目 | 值 |
| --- | --- |
| 连接超时 | 10 秒（`NetworkUtils.TIMEOUT`） |
| 读取超时 | 10 秒 |
| 每 URL 重试次数 | 5（`FetchTask.DEFAULT_RETRY`） |
| 重试间隔 | 200 毫秒 |
| 4xx | **不重试**，立即失败 |
| 非 2xx 非 3xx 非 4xx | 计入重试 |
| 并发下载 | `min(CPU 逻辑核数 × 4, 64)`，用户可在 HMCL 设置中调整 |

**服务端要求**：Servitium 必须能承受单客户端最多 64 路并发下载，且单个响应的首字节与后续字节间隔均不得超过 10 秒。大文件必须持续输出数据，不得先在服务端整体缓冲再发送。

## 7. Servitium 数据模型到协议的映射

Servitium 的 HMCL 分发采用三层模型（详见 `../design/mechanism-hmcl_distribution.md`）。此处仅定义其与协议的对应关系。

| Servitium 概念 | 协议对应 |
| --- | --- |
| **Archive**（归档） | 上传的 ZIP 包，经检查与过滤后成为一次 Release 的内容来源。协议中无对应物。 |
| **Release**（发布版本） | 不可变的一组文件 + 一份物化的 `server-manifest.json`。每个 Release 有独立、内容寻址的存储目录。协议中对应一份具体的 manifest 与其 `overrides/` 内容。 |
| **Channel**（通道） | 稳定 URL 前缀，指向「当前生效的 Release」。协议中对应 `fileApi` 的取值。 |
| Channel 的 slug | `fileApi` 路径中的稳定标识 |
| Release 的 `version` | manifest 的 `version` 字段（纯展示） |

**URL 布局（Servitium 固定方案）**

```text
{基址}/hmcl/{channel-slug}/server-manifest.json
{基址}/hmcl/{channel-slug}/overrides/{path}
```

因此 Release 中物化的 manifest 里，`fileApi` 恒为 `{基址}/hmcl/{channel-slug}`。

**原子发布要求**

由于客户端会在一次更新中先取 manifest、再逐个取文件，切换 Channel 指向必须保证：

1. Channel 指向的切换是单次原子操作（数据库内单事务更新指针）；
2. 切换后，旧 Release 的文件内容**必须仍可通过同一 Channel URL 取到**，直到没有任何进行中的更新会话为止。Servitium 的实现方式：`overrides/` 的服务端解析按「请求进入时锁定的 Release 版本」执行——客户端在读取 manifest 时会拿到一个 `ETag`，其值由 Release 标识派生；`overrides/` 请求按 Channel 当前 Release 解析，并在找不到时回退到最近 N 个历史 Release（N 默认 3）中查找同名同哈希文件。回退查找仅在按当前 Release 未命中时进行，且必须校验哈希一致，否则返回 404。
3. Release 的文件存储是内容寻址的，同一哈希的文件在多个 Release 间共享，因此上述回退在绝大多数情况下天然成立。

**回滚要求**

回滚即把 Channel 指针指向一个历史 Release，语义与发布完全相同，同样是原子指针切换。回滚后客户端下次启动会把本地文件同步回该历史 Release 的状态：新版新增的文件会被删除，被改过的文件会被改回（前提是用户未手工修改）。

## 8. 服务端响应规范（Servitium 必须实现）

### 8.1 `GET /hmcl/{channel}/server-manifest.json`

| 项目 | 规范 |
| --- | --- |
| 200 响应体 | UTF-8 JSON，字段顺序 `name, author, version, description, fileApi, files, addons` |
| `Content-Type` | `application/json; charset=utf-8` |
| `ETag` | 强 ETag，值由 Release 标识与 manifest 内容哈希派生，形如 `"<40位十六进制>"` |
| `Cache-Control` | **固定为 `no-cache`** |
| `Last-Modified` | Release 发布时间 |
| 条件请求 | 支持 `If-None-Match`，匹配时返回 `304`（不带正文，仍带 `ETag` 与 `Cache-Control: no-cache`） |
| Channel 不存在 | `404` |
| Channel 存在但无生效 Release | `404` |

`Cache-Control: no-cache` 是**强制**要求。`[RUNTIME]` 已证实：一旦返回正的 `max-age`，HMCL 会在该时长内完全不发起请求，导致玩家收不到已发布的更新，且服务端无法感知。

### 8.2 `GET|HEAD /hmcl/{channel}/overrides/{path}`

| 项目 | 规范 |
| --- | --- |
| 路径解码 | UTF-8 百分号解码；解码后必须经过路径归一化与逃逸检查 |
| 200 响应体 | 文件原始字节 |
| `Content-Type` | `application/octet-stream` |
| `Content-Length` | 必须准确 |
| `ETag` | 强 ETag，值为文件 SHA-1 |
| `Cache-Control` | `no-cache, no-transform`；不得设置未来的 `Expires` |
| `Accept-Ranges` | `bytes` |
| `Range` | 支持单区间，返回 `206` 与正确的 `Content-Range`；不支持多区间时返回 `200` 全量 |
| 无效 `Range` | `416`，带 `Content-Range: bytes */<总长>` |
| 条件请求 | 支持 `If-None-Match`、`If-Range` |
| 文件不在当前 Release 且回退未命中 | `404` |
| 路径逃逸 | `400` |

Release 的内部存储虽然按内容寻址且不可变，公开的 `overrides/` URL 却只包含 Channel 与相对路径；发布、回滚或历史回退都可能让同一 URL 返回不同字节。因此该 URL **不是**内容寻址 URL，不得使用正 `max-age` 或 `immutable`。强 ETag 必须等于文件 SHA-1，用于重校验、`If-Range` 与响应完整性标识；ETag 不能替代 `no-cache`，因为尚未发起的请求无法获知新 ETag。

`[RUNTIME]` 实测的文件下载请求未携带条件请求头，所以服务端不能依赖 HMCL 一定发送 `If-None-Match`。`no-transform` 与独立禁用响应压缩共同保证 `Range` 始终针对文件原始字节。

### 8.3 错误响应

HMCL 不解析错误响应正文。Servitium 仍统一返回 RFC 7807 Problem Details（`application/problem+json`），便于运维与 Servitium 自身的端到端测试。

| 状态码 | 触发条件 | 客户端后果 |
| --- | --- | --- |
| `400` | 路径非法 | 视为 4xx，立即失败，整个更新中止 |
| `403` | Channel 未公开 / 访问令牌无效 | 同上 |
| `404` | Channel 或文件不存在 | 同上 |
| `416` | Range 越界 | 客户端放弃续传并整重下 |
| `429` | 触发限流 | 计入重试，最多 5 次 |
| `500` / `502` / `503` | 服务端故障 | 计入重试，最多 5 次 |

**运营含义**：任何 4xx 都会阻止玩家启动游戏。因此 Servitium 必须在发布前保证 Release 内每一个 manifest 条目都能被成功读取（见发布前校验，`../design/mechanism-hmcl_distribution.md`）。

## 9. 安全要求

| 要求 | 规范 |
| --- | --- |
| 鉴权模型 | HMCL 不发送任何鉴权头。Channel 只有两种可见性：**公开**（任何人可读）与**令牌保护**（令牌作为 URL 路径的一部分：`/hmcl/{channel}/{token}/server-manifest.json`，且 `fileApi` 相应包含令牌）。 |
| 令牌 | 高熵随机串，存储为哈希；可轮换与吊销。轮换后旧令牌立刻失效，已安装的客户端会收到 `403` 并在下次启动时报错，必须由运营者重新分发链接。此代价需在界面上明示。 |
| 路径穿越 | 解码后必须归一化并确认仍位于 Release 根之下；拒绝 `..`、绝对路径、UNC 前缀、设备路径、备用数据流（ADS）后缀 |
| 符号链接 | Release 存储目录内不允许符号链接、目录联接与重解析点；上传归档解包时一律拒绝此类条目 |
| ZIP Slip | 归档解包必须逐条校验目标路径 |
| ZIP 炸弹 | 归档解包必须限制总解压大小、条目数与单条目压缩比 |
| 内容过滤 | 归档中不得包含服务端凭据类文件（见分发规格的黑名单） |
| 限流 | 按来源 IP 对 HMCL 端点限流；限流阈值必须高于「单客户端 64 路并发 + 5 次重试」的合理峰值 |
| SSRF | HMCL 端点为纯读取端点，不发起出站请求 |
| 日志 | 记录 Channel、Release、路径、状态码、字节数、来源 IP 前缀；不记录令牌明文 |
| TLS | 非回环暴露时必须启用受信 TLS 或声明可信反向代理模式（HMCL 使用 JDK 默认信任库，自签证书会导致连接失败） |

## 10. 服务端生成 manifest 的规则

Servitium 在 Release 物化时按下列规则生成 `server-manifest.json`：

1. 递归遍历 Release 根，仅登记常规文件；
2. 每个文件计算 SHA-1，输出小写十六进制；
3. `path` 使用相对 Release 根的路径，分隔符归一化为 `/`；
4. 按 `path` 的序数序（ordinal）排序，保证同内容 Release 生成字节一致的 manifest；
5. `fileApi` 设为 `{基址}/hmcl/{channel-slug}`，无尾部斜杠；
6. `addons` 由发布者在发布表单中显式声明（游戏版本必填；加载器可选，最多一个模组加载器 + 可选 OptiFine）；Servitium 不从文件内容自动推断加载器版本；
7. `name`、`author`、`version`、`description` 取自 Release 元数据；
8. 不输出 `downloadURL`，不输出任何扩展字段。

**排除规则**：Release 根下的以下条目在物化时被剔除且不进入 manifest（因为它们要么是客户端本地状态，要么会破坏玩家存档）：

```text
saves/            options.txt        servers.dat        usercache.json
usernamecache.json  logs/            crash-reports/     screenshots/
.fabric/          .mixin.out/        modpack.json       *.log
```

该列表在 `../design/mechanism-hmcl_distribution.md` 中作为可审计的固定常量维护，界面在发布预览中展示被剔除的条目。

## 11. 与首次安装的关系

首次安装走 `ServerModpackRemoteInstallTask`：

1. 客户端按 `addons` 构建游戏与加载器；
2. 随后立即执行一次 `ServerModpackCompletionTask`，其「旧 manifest」为传入的远端 manifest 但 `overrides` 为**空列表**；
3. 因此第 5 节算法中 `Lmap` 为空，所有 `R.files` 条目都走「新增 → 下载」分支，实现全量安装；
4. 安装失败时删除该实例目录；但若失败原因是 `ModpackCompletionException` 且非 `FileNotFoundException`，HMCL 会保留实例以便后续修复。

**结论**：首次安装与增量更新共用同一套服务端能力，Servitium 无需为首次安装提供任何额外端点。

## 12. 未定义边界的默认策略

协议未定义的情形，Servitium 一律采用「服务端预防」而非「依赖客户端容错」：

| 情形 | Servitium 策略 |
| --- | --- |
| manifest 中存在客户端无法下载的条目 | 发布前校验拦截，禁止发布 |
| 两个仅大小写不同的 `path` | 发布前校验拦截 |
| `path` 含 `..`、前导 `/`、`\`、控制字符 | 发布前校验拦截 |
| `path` 长度导致客户端侧超出 `MAX_PATH` | 发布前对超过 200 字符的相对路径发出警告，超过 240 字符拦截 |
| 未知加载器 | 发布表单仅允许七个已知 id |
| 空 `files` | 允许（等价于「清空所有受管理文件」），但界面必须二次确认 |
| Release 内单文件超过 2 GiB | 允许；服务端必须以流式方式响应 |

## 13. 相关文档

* `../research/investigation-hmcl_client.md`：客户端视角的场景行为与端到端测试依据
* `../design/mechanism-hmcl_distribution.md`：Servitium 侧产品规格
* `../reference/protocol-control_plane.md`：HMCL 端点在整体 API 中的位置
* `../design/security-threat_model.md`：令牌、路径与归档安全
