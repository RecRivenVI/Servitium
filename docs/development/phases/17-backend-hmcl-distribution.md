# 阶段 17：HMCL 分发后端

## 1. 阶段目的

实现完整的服务端整合内容分发能力：归档上传与检查、内容寻址存储、不可变 Release 与 manifest 物化、原子 Channel 发布与回滚、HMCL 兼容的分发端点、访问统计与完整性巡检。

## 2. 需求编号

`REQ-HMC-001` … `REQ-HMC-070`、`REQ-INS-050`、`REQ-INS-051`（HMCL 同步动作）、`REQ-SEC-050`（HMCL 归档）、`THR-060` … `THR-064`。

## 3. 前置条件

* 阶段 16 完成条件全部满足。
* 实现必须同时遵循 `docs/product/hmcl-distribution-specification.md`、`docs/reference/hmcl-auto-update-protocol.md` 与 `docs/reference/hmcl-client-behavior.md`。
* 阶段 00 的 HMCL 文档提供初始源码与运行证据；本阶段实现、测试或排查兼容性时，应按需前往 `D:\GitHubPublic\HMCL` 核实当前提交上的协议调用链和客户端行为。新证据若要求改变受保护协议，按 `AGENTS.md` 登记变更请求并等待裁决。

## 4. 范围

* Channel 与 Release 的数据模型与端点；
* 归档上传、安全检查、内容检查、内容过滤；
* 内容寻址存储与引用计数与回收；
* manifest 物化与自校验；
* 差异预览与发布门禁；
* 原子发布与回滚；
* HMCL 分发端点（独立中间件管线）；
* 历史回退查找；
* 令牌与可见性管理；
* 访问统计；
* 完整性巡检；
* 从实例采集内容与向实例同步内容。

## 5. 非目标

* 不实现前端界面（阶段 21）；
* 不实现真实 HMCL 客户端的界面级端到端验证（阶段 24；本阶段做协议级验证）。

## 6. 需要创建的项目或目录

```text
src/Servitium.Server/
├── Hmcl/
│   ├── Endpoints/          manifest 与 overrides 端点（独立管线）
│   ├── ChannelService
│   ├── ReleaseService      创建、物化、门禁
│   ├── PublishService      差异预览、原子发布、回滚
│   ├── ManifestWriter      manifest 生成（规则见协议规范第 10 节）
│   ├── AccessStats
│   └── IntegrityScanner
└── ...

src/Servitium.Infrastructure/
└── Content/                内容寻址存储、引用计数、回收

src/Servitium.Agent/
└── Distribution/           从实例采集内容、向实例同步内容
```

## 7. 需要实现的类型或模块

### 7.1 内容寻址存储

| 能力 | 说明 |
| --- | --- |
| 写入 | 落暂存 → 计算 SHA-1 → 目标已存在且长度一致则复用 → 否则原子移动 |
| 读取 | 按 SHA-1 打开流；支持 `Range` 定位 |
| 引用计数 | 在 Release 创建事务中增加；Release 淘汰事务中减少 |
| 回收 | 后台任务删除 `ReferenceCount = 0` 且创建超过 24 小时的对象；删除前二次确认计数仍为 0 |
| 布局 | `content\sha1\<前2位>\<完整40位>` |

### 7.2 Release 创建

```text
输入：归档 或 实例工作目录采集
1. 归档安全检查（REQ-SEC-050）
2. 解包到临时区（归档来源）或流式接收（实例来源）
3. 内容过滤（排除清单）
4. 内容检查（路径合法、大小写重复、路径长度、.disabled/.old、仅常规文件）
5. 逐文件计算 SHA-1 并写入内容库
6. 生成 manifest（排序、小写哈希、fileApi、addons）
7. 自校验：每条目在内容库存在且哈希匹配
8. 在单事务中创建 Release、文件映射、引用计数
```

任一步失败即中止并清理临时区；已写入内容库的对象由引用计数为 0 的回收任务处理。

### 7.3 manifest 生成规则

严格按协议规范第 10 节：

* 字段顺序 `name, author, version, description, fileApi, files, addons`；
* `fileApi` 无尾部斜杠；
* `files` 按 `path` 序数序排序；
* `path` 使用正斜杠相对路径；
* `hash` 小写 40 位十六进制；
* 不输出 `downloadURL`，不输出扩展字段；
* `addons` 必含 `game`；加载器仅允许七个已知 id。

生成结果作为字节固化在 Release 上，端点直接返回该字节，保证 ETag 稳定。

### 7.4 发布门禁

```text
1. manifest 每条目在内容库中可读
2. 哈希与内容库记录一致
3. 无仅大小写不同的重复路径
4. 无非法路径
5. 路径长度未超限
6. 加载器声明合法（仅七个已知 id，至多一个模组加载器 + 可选 optifine，必含 game）
```

任一失败即拒绝并列出**全部**问题（不在首个失败处中止）。

### 7.5 差异预览

与 Channel 当前 Release 比较，输出三组：

| 组 | 判定 |
| --- | --- |
| 将新增 | 路径在新 Release 中而不在当前 Release 中 |
| 将覆盖 | 路径在两者中且哈希不同 |
| 将删除 | 路径在当前 Release 中而不在新 Release 中 |

另输出加载器变更标记（`addons` 映射是否相等）。

### 7.6 原子发布与回滚

在单个数据库事务中：校验门禁 → 更新 Channel 指针 → 写审计 → 提交。发布与回滚共用同一实现。

发布后触发：`hmcl.published` 事件；历史 Release 超出保留数时的淘汰（在事务之外的后台任务中执行）。

### 7.7 分发端点（独立管线）

```text
/hmcl/{channel}[/{token}]/server-manifest.json
/hmcl/{channel}[/{token}]/overrides/{**path}
```

**独立中间件管线**，不经 CSRF、CORS、会话认证与全局压缩配置。

| 端点 | 规范 |
| --- | --- |
| manifest | `200` 返回固化字节；`Content-Type: application/json; charset=utf-8`；强 `ETag`；**`Cache-Control: no-cache`**；`Last-Modified`；支持 `If-None-Match` 与 `304`；Channel 不存在或无生效 Release 返回 `404`；令牌无效返回 `403` |
| overrides | UTF-8 百分号解码；按当前 Release 的路径映射查 SHA-1；未命中则历史回退查找（最近 3 个）；`Content-Type: application/octet-stream`；准确 `Content-Length`；强 `ETag` = 文件 SHA-1；`Cache-Control: no-cache, no-transform`；不设未来的 `Expires`；`Accept-Ranges: bytes`；支持 `Range` 与 `206`；越界 `416`；**恒不压缩**；支持 `HEAD` |

### 7.8 令牌与可见性

* 令牌：32 字节随机的 Base64URL（43 字符），只存 SHA-256；
* 轮换：生成新令牌，旧令牌立即失效；
* 可见性切换：两个方向都使旧链接失效；
* 全部操作写审计。

### 7.9 访问统计

按 Channel × Release × 日期聚合：manifest 请求数（按状态码）、文件请求数（按状态码）、传输字节、唯一来源数（IPv4 /24、IPv6 /48 前缀哈希后计数）、错误计数。

**不存储完整 IP。**

### 7.10 完整性巡检

每 24 小时校验每个保留中的 Release：manifest 条目在内容库存在且哈希匹配。发现不一致时推送 `hmcl.integrity-alert` 并在 Channel 上置状态。

### 7.11 实例集成

| 能力 | 说明 |
| --- | --- |
| 从实例采集 | Agent 遍历实例工作目录（应用排除清单），通过 `content-collect` 子流把文件流式送到控制平面的内容库 |
| 向实例同步 | 控制平面通过 `content-sync` 子流把 Release 内容送到 Agent；Agent 按客户端更新算法的等价语义应用：新增下载、变更覆盖、删除移除、不受管理文件保留 |

「向实例同步」是实例更新动作之一（`REQ-INS-050`）。实例必须处于 `Stopped`。

## 8. 数据模型

控制平面新增：`HmclChannel`、`HmclRelease`、`HmclReleaseFile`、`HmclContentObject`、`HmclArchive`、`HmclAccessStat`。

## 9. API 或事件

REST：`protocols-and-contracts.md` 3.13 的全部端点。

分发端点：8.7。

事件：`hmcl.release-created`、`hmcl.published`、`hmcl.integrity-alert`。

gRPC：`CollectDirectoryForRelease`、`SyncFromRelease` 与两条子流。

## 10. 实施顺序

1. 内容寻址存储（写入、读取、引用计数、回收）；
2. `HmclChannel` 与 `HmclRelease` 数据模型与迁移；
3. 归档上传（复用阶段 15 的上传会话）；
4. 归档安全检查（复用阶段 13 实现）；
5. 内容过滤（排除清单）；
6. 内容检查；
7. `ManifestWriter` + 自校验；
8. Release 创建事务；
9. 发布门禁；
10. 差异预览；
11. 原子发布与回滚；
12. 历史保留与淘汰；
13. 分发端点：独立管线 + manifest；
14. 分发端点：overrides（含 `Range` 与 `HEAD`）；
15. 历史回退查找；
16. 令牌与可见性；
17. 访问统计；
18. 完整性巡检；
19. Agent 侧从实例采集；
20. Agent 侧向实例同步；
21. `hmcl` 端点；
22. 测试（含协议级真实客户端验证，见第 15.1 节）；
23. 运行 `eng/Validate.ps1`。

## 11. 关键算法

| 算法 | 说明 |
| --- | --- |
| manifest 生成 | 见 8.3；必须字节确定：相同内容集合与相同元数据生成完全相同的字节 |
| 内容过滤 | 逐条目与排除清单比对（目录前缀匹配 + 精确名 + 通配后缀），记录被剔除项与原因 |
| 大小写重复检测 | 用不区分大小写的比较器建集合，冲突即报错并列出冲突组 |
| 历史回退查找 | 按 Channel 取最近 3 个非当前 Release（按序号降序），逐个查同路径；命中后校验哈希与该 Release 记录一致 |
| `Range` 处理 | 解析单区间；`bytes=a-b`、`bytes=a-`、`bytes=-n` 三种形式；越界返回 `416` 并带 `Content-Range: bytes */<总长>`；多区间降级为 `200` 全量 |
| 引用计数回收 | 后台任务扫描 `ReferenceCount = 0` 且 `CreatedAt < now - 24h`；删除前在事务中再次确认计数为 0 |
| 唯一来源计数 | 取 IP 前缀 → 加盐哈希 → 按日去重计数（使用集合或基数估计结构） |
| 向实例同步 | 与协议规范第 5 节的客户端算法等价：以实例当前的「上次同步清单」为旧 manifest；无清单时视为空 |

## 12. Windows 平台事项

* 内容库路径较深，注意长路径；
* 大文件流式读写，避免整体缓冲（`REQ-HMC` 允许单文件超过 2 GiB）；
* 原子移动使用 `MoveFileEx`；
* 分发端点的静态文件读取使用异步流并设置合理的缓冲区。

## 13. 安全要求

| 威胁 | 应对 |
| --- | --- |
| `THR-060` 未授权访问 | 令牌哈希存储、可轮换、可吊销 |
| `THR-061` 路径穿越 | `overrides/{path}` 是清单中的**键**而非文件系统路径；仍对 `path` 做规范校验 |
| `THR-062` 放大攻击 | 按 IP 限流（阈值高于 64 并发 × 5 重试）；`HEAD` 不返回正文 |
| `THR-063` 恶意归档 | 归档安全检查 + 内容检查 + 发布门禁 |
| `THR-064` 缓存静默停更 | manifest 端点 `Cache-Control` 恒为 `no-cache`，且该端点有独立管线不受全局配置影响 |

令牌不得出现在日志中（只记录 Channel 与是否携带令牌）。

## 14. 自动化测试

单元测试：

| 编号族 | 内容 |
| --- | --- |
| `UNT-500` | manifest 生成的字节确定性与字段顺序 |
| `UNT-510` | 排除清单匹配 |
| `UNT-520` | 大小写重复检测 |
| `UNT-530` | 差异计算（新增/覆盖/删除/加载器变更） |
| `UNT-540` | 加载器声明校验 |
| `UNT-550` | `Range` 解析（含三种形式与越界） |

## 15. 集成测试

| 编号族 | 内容 |
| --- | --- |
| `INT-1300` | 归档上传 → 检查 → 过滤 → 物化 → Release 创建 |
| `INT-1310` | 恶意归档被拒（复用阶段 13 的用例集） |
| `INT-1320` | 内容检查：大小写重复、超长路径、`.disabled` 条目被拒 |
| `INT-1330` | 物化自校验：构造哈希不符的情况被拒 |
| `INT-1340` | 发布门禁：缺文件、哈希不符、非法加载器均被拒且列出全部问题 |
| `INT-1350` | 差异预览正确 |
| `INT-1360` | 原子发布：并发发布最终一致；无中间态可见 |
| `INT-1370` | 回滚：Channel 指针回到历史 Release |
| `INT-1380` | 历史保留与淘汰；仍被引用的内容对象不被回收 |
| `INT-1390` | **manifest 端点响应头**：`Cache-Control: no-cache`、强 ETag、`304` 生效、全局压缩开启时仍不影响 |
| `INT-1400` | **overrides 端点**：`ETag` = 文件 SHA-1、`Cache-Control: no-cache, no-transform`、无未来 `Expires`、`Accept-Ranges`、`Range` 与 `206`、`416`、`HEAD`、**无 `Content-Encoding`** |
| `INT-1410` | **UTF-8 百分号编码路径**：中文目录、中文文件名、含空格文件名 |
| `INT-1420` | 历史回退查找：发布切换后旧路径仍可取到 |
| `INT-1430` | 令牌：正确令牌可访问；错误令牌 `403`；轮换后旧令牌失效 |
| `INT-1440` | 可见性切换：两个方向都使旧链接失效 |
| `INT-1450` | 访问统计：请求被计入；不存储完整 IP |
| `INT-1460` | 完整性巡检：人为删除内容对象后被检出并告警 |
| `INT-1470` | 从实例采集：排除清单生效；文件哈希一致 |
| `INT-1480` | 向实例同步：新增/覆盖/删除/不受管理文件保留，与协议算法一致 |

安全测试：`SEC-165`、`SEC-250`、`SEC-251`、`SEC-260`、`SEC-270`、`SEC-280`、`SEC-281`。

### 15.1 协议级真实客户端验证

本阶段必须用**真实 HMCL 客户端**完成协议级验证（界面级验证留到阶段 24）。方式：

1. 在测试环境创建 Channel 并发布一个 Release；
2. 用真实 HMCL 客户端安装该整合内容；
3. 发布新 Release（含新增、修改、删除三类变化）；
4. 触发客户端更新并核对本地文件结果；
5. 覆盖 `docs/reference/hmcl-client-behavior.md` 第 9 节的测试映射表中除「界面呈现」外的全部项。

若当前环境无法运行 HMCL 客户端（缺少 Java 或图形环境），必须**停止并请求用户协助**，不得以「已有协议规范」为由跳过。

## 16. 人工测试

第 15.1 节的真实客户端验证若需要图形环境，按需请求用户协助。

## 17. 调试启动方式

```powershell
dotnet run --project src/Servitium.Server -- --environment Development
dotnet run --project src/Servitium.Agent -- --environment Development
# 通过 OpenAPI 创建 Channel、上传归档、发布
# 用 curl 或 HMCL 客户端访问：
#   https://127.0.0.1:5344/hmcl/<slug>/server-manifest.json
```

## 18. 验收责任方

编码 Agent 自主验收；真实客户端验证若需图形环境则请求用户协助。

## 19. 完成条件

1. Channel 与 Release 的完整生命周期可用；
2. 归档检查、内容检查、物化自校验、发布门禁全部生效且列出全部问题；
3. 原子发布与回滚正确；
4. **manifest 端点恒为 `no-cache`**（`INT-1390`）；
5. **overrides 端点恒不压缩且支持 `Range` 与强 ETag**（`INT-1400`）；
6. UTF-8 路径正确（`INT-1410`）；
7. 历史回退查找生效；
8. 令牌与可见性管理生效；
9. 访问统计不存储完整 IP；
10. 完整性巡检可检出不一致；
11. 从实例采集与向实例同步正确；
12. 第 15.1 节的真实客户端协议级验证通过；
13. `INT-1300` … `INT-1480` 全部通过；
14. 本阶段列出的全部 `SEC-*` 通过；
15. `eng/Validate.ps1` 全量通过。

## 20. 禁止提前进入下一阶段的条件

* manifest 端点的缓存头不符合规范；
* overrides 端点启用了压缩或不支持 `Range`；
* 发布门禁存在可绕过的路径；
* 真实客户端协议级验证未完成（含因缺环境而未完成——此时应请求用户协助而非跳过）；
* 内容回收会删除仍被引用的对象。

## 21. 需要更新的动态记录

* `docs/records/current-status.md`
* `docs/records/development-log.md`
* `docs/records/decision-log.md`
* `docs/records/problem-log.md`
* `docs/records/validation-log.md`（记录真实客户端验证的完整过程与观察）
* `docs/testing/requirements-traceability.md`
* `docs/testing/acceptance-matrix.md`
