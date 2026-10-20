# HMCL 整合内容分发产品规格

本文件定义 Servitium 面向 HMCL 客户端的整合内容分发能力的产品行为。协议层事实见 `../reference/protocol-hmcl_update.md`；客户端场景行为见 `../research/investigation-hmcl_client.md`。需求编号见 `../reference/api-functional_requirements.md` 第 13 节。

## 1. 能力陈述

服务器运营者把一份服务端整合内容（模组、配置、资源）发布到 Servitium，玩家在 HMCL 中一次性安装后，每次启动游戏时客户端自动与服务器保持同步：新增文件被下载、变更文件被更新、被移除的文件被删除，玩家自己的改动被保留。

## 2. 数据模型

### 2.1 三层结构

```text
Channel（通道，稳定 URL）
   └─ 指向 → Release（发布版本，不可变）
                  └─ 内容 ← Archive（归档，一次上传）
```

| 概念 | 定义 | 可变性 |
| --- | --- | --- |
| **Archive** | 一次上传的 ZIP 归档，经安全检查与内容过滤后成为 Release 的内容来源 | 上传后不可修改；创建 Release 后可删除原始归档以回收空间 |
| **Release** | 一组文件 + 一份物化的 `server-manifest.json` + 元数据 | **完全不可变** |
| **Channel** | 稳定的对外 URL 前缀，持有一个指向某个 Release 的指针 | 指针可切换；slug 创建后不可改 |

### 2.2 Channel 属性

| 属性 | 说明 | 约束 |
| --- | --- | --- |
| slug | URL 中的稳定标识 | 3–48 字符，`[a-z0-9-]`，唯一，创建后不可修改 |
| 显示名 | 界面展示 | 1–64 字符 |
| 描述 | 说明文本 | ≤ 1000 字符 |
| 可见性 | `公开` 或 `令牌保护` | 可切换 |
| 令牌 | 令牌保护时的访问令牌 | 只存哈希，可轮换 |
| 当前 Release | 指针 | 可为空（此时端点返回 404） |
| 保留数量 | 保留的历史 Release 数 | 默认 10，最小 3 |
| 关联实例 | 可选 | 用于「从实例创建 Release」 |

### 2.3 Release 属性

| 属性 | 说明 |
| --- | --- |
| 标识 | `Guid` |
| 序号 | Channel 内单调递增的整数，用于展示 |
| 名称 | 写入 manifest 的 `name` |
| 作者 | 写入 manifest 的 `author` |
| 版本 | 写入 manifest 的 `version`（纯展示，不参与更新判定） |
| 描述 | 写入 manifest 的 `description` |
| 游戏版本 | 必填，写入 `addons` 中 `id = "game"` 的条目 |
| 加载器 | 可选，最多一个模组加载器（`forge`/`neoforge`/`fabric`/`quilt`/`liteloader`）+ 可选 `optifine` |
| 文件数与总大小 | 统计值 |
| 创建时间与创建者 | 审计信息 |
| 内容清单 | 路径 → SHA-1 的有序列表 |
| 物化 manifest | 生成后固化的 JSON 字节 |

### 2.4 内容存储

Release 的文件以**内容寻址**方式存储：文件内容按 SHA-1 分桶存放在 Servitium 数据目录下的内容库中；Release 只保存「路径 → SHA-1」的映射。相同内容的文件在多个 Release 之间共享，因此保留多个历史 Release 的额外成本仅为发生变化的文件。

内容对象在没有任何保留中的 Release 引用时被回收。回收是幂等的后台任务，且必须在 Channel 指针切换事务之外执行。

## 3. 发布流程

### 3.1 从归档创建 Release

1. **上传**：分片上传 ZIP 归档（长操作）。
2. **归档安全检查**（`REQ-SEC-050`）：ZIP Slip、绝对路径、UNC、设备路径、`..`、符号链接与硬链接、条目总数、解压总大小、单条目压缩比、目录深度、非法字符与保留设备名。任一失败即中止并列出问题条目。
3. **解包**到临时区。
4. **内容过滤**：按第 4 节的排除清单剔除条目，记录被剔除项与原因。
5. **内容检查**：
   * 路径不含 `..`、前导 `/`、`\`；
   * 不存在仅大小写不同的重复路径；
   * 相对路径长度 ≤ 240 字符（> 200 时警告）；
   * 不存在 `*.disabled` 与 `*.old` 条目（这些在客户端侧有特殊语义）；
   * 仅包含常规文件。
6. **元数据表单**：名称、作者、版本、描述、游戏版本（必填）、加载器（可选）。
7. **物化**：计算每个文件的 SHA-1，写入内容库，生成 `server-manifest.json`（生成规则见协议规范第 10 节），并自校验每个条目在内容库中存在且哈希匹配。
8. **Release 创建完成**，状态为「已就绪，未发布」。

### 3.2 从实例工作目录创建 Release

与 3.1 相同，但内容来源是 Agent 上某个实例的工作目录。实例必须处于 `Stopped`。Agent 按排除清单遍历并把文件流式传输到控制平面的内容库。

### 3.3 发布

1. **差异预览**（`REQ-HMC-005`）：与 Channel 当前 Release 比较，展示三组清单：
   * **将新增**：数量 + 路径列表；
   * **将覆盖**：数量 + 路径列表；
   * **将删除**：数量 + 路径列表，以危险样式呈现；
   * **加载器变更**：若 `addons` 映射发生变化，以醒目提示说明「玩家客户端将重新下载游戏与加载器」。
2. **发布门禁**（`REQ-HMC-012`）：manifest 每条目在内容库中可读、哈希匹配、无大小写重复、无非法路径、路径长度未超限、加载器声明合法。任一失败即拒绝并列出全部问题。
3. **确认**：确认对话框展示三组数量；当「将删除」非空或加载器变更时，要求键入 Channel 显示名确认。
4. **原子切换**：在单个数据库事务中把 Channel 指针指向新 Release，并写入审计。
5. **生效**：立即生效，无需重启。

### 3.4 回滚

把 Channel 指向任意保留中的历史 Release。流程与 3.3 完全相同（同样的预览、门禁、确认与原子切换）。界面上明确标注这是回滚操作及目标 Release 的序号与创建时间。

### 3.5 发布瞬间的一致性

客户端的一次更新会先取 manifest 再逐个取文件。若在这期间发生发布切换，客户端持有的是旧 manifest，而 `overrides/` 已指向新 Release。

Servitium 的处置（`REQ-HMC-032`）：`overrides/` 请求在当前 Release 中未命中时，在最近 3 个历史 Release 中按同路径查找，命中且哈希与该历史 Release 的记录一致时返回。由于内容寻址存储的存在，绝大多数文件在切换前后是同一个内容对象，回退查找实际很少触发。

未命中回退窗口的客户端会收到 `404`，其后果是本次启动被阻止（`HCB-011`），下次启动时会重新拉取 manifest 并成功。该风险已知且可接受；界面在发布确认中不做额外提示，但文档中说明建议在低峰期发布。

## 4. 内容排除清单

下列条目在从归档或实例创建 Release 时被剔除，**不会**进入 manifest。清单为固定常量，界面在发布预览中列出被剔除的实际条目。

### 4.1 客户端本地状态（剔除，避免覆盖玩家数据）

```text
saves/                 options.txt            optionsof.txt
optionsshaders.txt     servers.dat            servers.dat_old
usercache.json         usernamecache.json     screenshots/
logs/                  crash-reports/         .fabric/
.mixin.out/            .optifine/             realms_persistence.json
```

### 4.2 启动器与整合包元数据（剔除，避免与客户端管理冲突）

```text
modpack.json           manifest.json          minecraftinstance.json
modrinth.index.json    instance-game-settings.json
hmclversion.cfg        launcher_profiles.json launcher_accounts.json
versions/              libraries/             assets/
```

### 4.3 服务端专有与凭据类（剔除，避免泄露）

```text
server.properties      ops.json               whitelist.json
banned-players.json    banned-ips.json        eula.txt
*.log                  *.log.gz               world/
world_nether/          world_the_end/         backups/
```

`server-icon.png` 不属于剔除清单并予以保留；它不含凭据，也不会与客户端管理的数据冲突。过滤预览仍把它标记为服务端展示资源，提醒运营者确认是否确有分发必要。

### 4.4 有特殊客户端语义（剔除，避免破坏客户端行为）

```text
**/*.disabled          **/*.old
```

### 4.5 覆盖规则

排除清单不可由用户修改。若运营者确实需要分发某个被剔除的文件，界面提示改用不同的文件名。

## 5. URL 布局

### 5.1 公开 Channel

```text
{基址}/hmcl/{channel-slug}/server-manifest.json
{基址}/hmcl/{channel-slug}/overrides/{path}
```

manifest 中的 `fileApi` = `{基址}/hmcl/{channel-slug}`（无尾部斜杠）。

### 5.2 令牌保护 Channel

```text
{基址}/hmcl/{channel-slug}/{token}/server-manifest.json
{基址}/hmcl/{channel-slug}/{token}/overrides/{path}
```

`fileApi` = `{基址}/hmcl/{channel-slug}/{token}`。

令牌为 32 字节随机值的 base64url 编码（43 字符）。服务端只存哈希（SHA-256）。

### 5.3 基址

`{基址}` 由系统设置中的「对外基址」决定。未配置时使用请求的 `Host`（可信代理模式下使用 `X-Forwarded-Host`）。Channel 详情页展示完整链接并提供复制按钮，链接**必须**以 `server-manifest.json` 结尾（`REQ-HMC-041`）。

## 6. HTTP 响应规范

见协议规范第 8 节。此处仅列出对产品行为有影响的强制项：

| 端点 | 强制规范 | 理由 |
| --- | --- | --- |
| manifest | `Cache-Control: no-cache`，不设未来的 `Expires` | 否则客户端在缓存期内完全不请求，运营者发布的更新对玩家不可见且服务端无感知（`HCB-018` 已运行验证） |
| manifest | 强 `ETag`，支持 `If-None-Match` 与 `304` | 减少无变化时的传输 |
| overrides | 强 `ETag` = 文件 SHA-1 | 提供稳定的内容校验标识并支持条件请求与 `If-Range` |
| overrides | `Cache-Control: no-cache, no-transform`，不设未来的 `Expires` | Channel URL 在发布与回滚后可能指向不同内容，必须重校验；同时禁止中间层转换正文 |
| overrides | 恒不压缩 | 客户端断点续传要求 identity 编码 |
| overrides | `Accept-Ranges: bytes` 且支持 `Range` | 弱网可靠性 |
| 全部 | 4xx 会阻止玩家启动游戏 | 因此发布门禁必须在服务端拦截一切可能导致 4xx 的情况 |

## 7. 可见性与令牌管理

### 7.1 切换可见性

* 公开 → 令牌保护：生成令牌，旧的公开链接立即失效，已安装玩家将无法启动。以破坏性操作对待。
* 令牌保护 → 公开：令牌链接继续有效（令牌段被忽略）还是失效？**固定为失效**——切换后只有不含令牌段的 URL 有效，已安装玩家仍需重新安装。同样以破坏性操作对待。

两个方向的切换都要求二次确认并键入 Channel 显示名。

### 7.2 令牌轮换

轮换生成新令牌并立即使旧令牌失效。确认对话框固定文案要点：

* 所有已安装该整合内容的玩家将在下次启动时收到错误；
* 玩家必须使用新链接重新安装；
* 该操作不可撤销。

轮换写入安全审计。

## 8. 与实例的关联

实例可关联一个 Channel（`REQ-HMC-060`）。关联后：

| 能力 | 说明 |
| --- | --- |
| 实例详情展示 | Channel 名、当前 Release 序号与版本、上次发布时间 |
| 从实例创建 Release | 以该实例工作目录为内容来源走 3.2 流程 |
| 同步到实例 | 把 Channel 当前 Release 的内容同步到实例工作目录（实例必须停止）。语义与客户端更新算法一致：新增下载、变更覆盖、删除移除，实例本地新增的不受管理文件保留 |

「同步到实例」是实例的更新动作之一（`REQ-INS-050`）。

## 9. 访问统计

按 Channel 与 Release 统计（`REQ-HMC-050`）：

| 指标 | 说明 |
| --- | --- |
| manifest 请求数 | 按状态码分（200 / 304 / 4xx / 5xx） |
| 文件请求数 | 同上 |
| 传输字节数 | 实际发送的正文字节 |
| 唯一来源数 | 按 IP 前缀聚合（IPv4 /24、IPv6 /48），**不存储完整 IP** |
| 错误计数 | 按状态码与路径聚合，用于发现清单与存储不一致 |

展示最近 7 天与 30 天的趋势。

统计数据保留 90 天。

## 10. 界面

页面结构见 `../design/architecture-information.md`。此处仅明确产品级要求：

| 页面 | 关键内容 |
| --- | --- |
| 分发总览（PG-060） | Channel 列表：显示名、slug、可见性、当前 Release 序号与版本、文件数、上次发布时间、最近 7 天请求数、状态 |
| Channel 详情（PG-061） | 分发链接（可复制，以 `server-manifest.json` 结尾）、当前 Release 摘要、Release 历史列表、访问统计图表、可见性与令牌管理、关联实例 |
| 归档上传与发布（PG-062） | 上传 → 检查结果 → 过滤预览（被剔除条目与原因）→ 元数据表单 → 差异预览 → 发布确认 |
| Release 详情（PG-063） | 元数据、文件清单（可搜索、可分页）、manifest 预览（只读 JSON）、回滚入口、被哪些历史发布使用过 |

## 11. 失败与恢复

| 情况 | Servitium 行为 |
| --- | --- |
| 归档安全检查失败 | 中止，列出全部问题条目与原因，不创建 Release |
| 内容检查失败 | 中止，列出全部问题条目 |
| 物化时哈希不匹配 | 中止并回滚已写入的内容对象引用；记录错误 |
| 发布门禁失败 | 拒绝发布，列出全部问题；Channel 指针不变 |
| 发布事务失败 | 完全回滚，Channel 指针不变 |
| 内容库中文件丢失（外部原因） | manifest 端点仍返回旧内容，但文件端点返回 404；后台完整性巡检任务定期校验并在发现不一致时推送告警 |
| 令牌被吊销后仍有请求 | 返回 `403`，计入错误统计 |

## 12. 完整性巡检

后台任务定期（默认每 24 小时）校验：每个保留中的 Release 的 manifest 条目在内容库中存在且哈希匹配。发现不一致时：

* 推送告警事件；
* 在分发总览中以危险样式标注受影响的 Channel；
* 若受影响的是当前生效的 Release，提示运营者立即回滚或重新发布。

巡检结果写入 `../development/testing-development.md` 的运行时对应物（运维日志），不修改任何内容。

## 13. 验收要点

| 项 | 判定 |
| --- | --- |
| manifest 缓存头 | 契约测试断言恒为 `no-cache` |
| overrides 不压缩 | 契约测试断言无 `Content-Encoding` |
| Unicode 路径 | 端到端测试含中文目录、中文文件名、含空格文件名 |
| 发布门禁 | 构造缺文件、哈希不符、大小写重复、超长路径、非法加载器的 Release，断言发布被拒 |
| 原子发布 | 并发发布测试断言 Channel 指针最终一致且无中间态可见 |
| 回滚 | 端到端用真实 HMCL 验证回滚后客户端状态回到历史 Release |
| 令牌轮换 | 端到端断言旧链接返回 403 |
| 内容回收 | 断言淘汰历史 Release 后，仍被引用的内容对象不被删除 |
| 真实客户端 | `../research/investigation-hmcl_client.md` 第 9 节全部测试项通过 |
