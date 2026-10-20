# 数据与存储

## 1. 原则

1. **两个数据库**：控制平面一个 SQLite 数据库，每个 Agent 一个本地 SQLite 数据库。二者结构不同、职责不同，不做双向复制。
2. **大文件不入库**：归档、内容库对象、备份、上传暂存、日志文件一律存于文件系统；数据库只存元数据与路径。
3. **权威副本明确**：每类数据只有一个权威所有者，另一侧是镜像。
4. **UTC 存储**：所有时间列为 `DateTimeOffset`，以 UTC 写入。
5. **迁移可重复**：EF Core 迁移，向前兼容，不写破坏性的手工 SQL。

## 2. 目录布局

### 2.1 控制平面数据目录

默认 `%ProgramData%\Servitium\Server`，可在安装时指定。

```text
%ProgramData%\Servitium\Server\
├── servitium.db                数据库
├── servitium.db-wal
├── servitium.db-shm
├── config\
│   └── appsettings.Production.json    敏感值经 DPAPI 加密
├── content\                    HMCL 内容库（内容寻址）
│   └── sha1\<前2位>\<完整哈希>
├── archives\                   已上传的 HMCL 归档
│   └── <archiveId>.zip
├── uploads\                    上传分片暂存
│   └── <uploadId>\<index>.part
├── assets\                     Logo、背景图、提供方图标
├── exports\                    诊断导出与日志导出的临时产物
└── logs\
    └── server-<yyyyMMdd>.log
```

### 2.2 Agent 数据目录

默认 `%ProgramData%\Servitium\Agent`。

```text
%ProgramData%\Servitium\Agent\
├── agent.db                    本地数据库
├── config\
│   └── agent.json              含节点凭据引用；凭据本体经 DPAPI 加密
├── instances\                  默认实例基路径（可配置到其他位置）
│   └── <instanceId>\           工作目录（OwnedByServitium = true）
├── java\                       导入型 Java 运行时
│   └── <runtimeId>\
├── backups\                    配置写入前备份
│   └── <instanceId>\<相对路径转义>\<时间戳>.bak
├── staging\                    上传与解压临时区
└── logs\
    └── agent-<yyyyMMdd>.log
```

### 2.3 ACL

| 路径 | ACL |
| --- | --- |
| 两个数据目录根 | `SYSTEM` 与 `Administrators` 完全控制；服务账户完全控制；`Users` 无权限 |
| `config\` | 仅服务账户与 `Administrators` 读写 |
| `content\`、`archives\` | 服务账户读写 |
| 实例工作目录 | 服务账户完全控制；不放宽给普通用户 |
| 外部工作目录 | 由用户自行管理；Agent 只要求可读写 |

安装器负责设置 ACL（`../development/build-packaging.md`）。

## 3. 控制平面数据库

### 3.1 实体一览

| 实体 | 说明 | 权威副本 |
| --- | --- | --- |
| `User` | 用户 | 控制平面 |
| `UserCredential` | 密码哈希与参数 | 控制平面 |
| `TotpSecret` | TOTP 密钥与恢复码哈希 | 控制平面 |
| `ApiKey` | API Key 哈希与元数据 | 控制平面 |
| `ExternalIdentity` | `(issuer, subject)` 与用户的绑定 | 控制平面 |
| `WindowsIdentityBinding` | Windows SID 与用户的绑定 | 控制平面 |
| `Session` | 会话 | 控制平面 |
| `Node` | 节点登记 | 控制平面 |
| `NodeCredential` | 节点凭据哈希 | 控制平面 |
| `NodeRegistrationToken` | 一次性注册令牌哈希 | 控制平面 |
| `NodeMetricSample` | 节点指标采样（滚动保留 24 小时） | 控制平面 |
| `Instance` | 实例镜像 | **Agent** |
| `InstanceGrant` | 用户 × 实例授权 | 控制平面 |
| `JavaRuntime` | Java 运行时镜像 | **Agent** |
| `ScheduledTask` | 计划任务镜像 | **Agent** |
| `ScheduledTaskRun` | 执行历史镜像 | **Agent** |
| `Operation` | 长操作 | 控制平面 |
| `ActivityLog` | 操作日志 | 控制平面 |
| `AuditLog` | 安全审计日志（只追加） | 控制平面 |
| `SystemSetting` | 系统设置（单行） | 控制平面 |
| `DashboardLayout` | 按用户的仪表盘布局 | 控制平面 |
| `HmclChannel` | Channel | 控制平面 |
| `HmclRelease` | Release | 控制平面 |
| `HmclReleaseFile` | Release 的路径 → SHA-1 映射 | 控制平面 |
| `HmclContentObject` | 内容库对象与引用计数 | 控制平面 |
| `HmclArchive` | 已上传归档 | 控制平面 |
| `HmclAccessStat` | 按日聚合的访问统计 | 控制平面 |
| `UploadSession` | 分片上传会话与位图 | 控制平面 |
| `IpBan` | 临时封禁 | 控制平面 |

### 3.2 关键约束

| 实体 | 约束 |
| --- | --- |
| `User` | `UserName` 唯一（不区分大小写）；`Role = Owner` 的行至多一条且不可删除 |
| `ExternalIdentity` | `(Issuer, Subject)` 唯一；`UserId` 上有唯一索引（一个用户至多绑定一个外部身份） |
| `WindowsIdentityBinding` | `Sid` 唯一 |
| `ApiKey` | `KeyHash` 唯一 |
| `Node` | `Name` 唯一；`RegistrationTokenHash` 唯一（未消费的） |
| `Instance` | `(NodeId, Id)`；`Name` 在节点内唯一 |
| `InstanceGrant` | `(UserId, InstanceId)` 唯一 |
| `HmclChannel` | `Slug` 唯一 |
| `HmclRelease` | `(ChannelId, Sequence)` 唯一 |
| `HmclReleaseFile` | `(ReleaseId, Path)` 唯一；`Path` 存储为归一化后的正斜杠相对路径 |
| `HmclContentObject` | `Sha1` 主键；`ReferenceCount ≥ 0` |
| `AuditLog` | 无更新与删除路径；仅按保留期由专门的清理作业删除超期行，该作业本身写审计 |

### 3.3 SQLite 配置

| 项 | 值 | 理由 |
| --- | --- | --- |
| `journal_mode` | `WAL` | 读写并发 |
| `synchronous` | `NORMAL` | WAL 下的合理权衡 |
| `foreign_keys` | `ON` | 引用完整性 |
| `busy_timeout` | 5000 ms | 降低瞬时锁冲突 |
| 写入并发 | 应用层单写入队列 | SQLite 单写者模型 |
| 连接池 | 读连接池化，写连接单例 | 同上 |

### 3.4 迁移

* 使用 EF Core 迁移；每次结构变更生成一个迁移；
* 服务启动时自动应用迁移，失败则拒绝启动并输出明确错误；
* 迁移前自动备份数据库文件到 `%ProgramData%\Servitium\Server\backups\db-<版本>-<时间戳>.db`；
* 不编写向后（Down）迁移的业务逻辑，回滚通过还原备份实现；
* 迁移必须可在包含数据的库上重复应用（幂等检查由测试守护）。

## 4. Agent 本地数据库

### 4.1 实体一览

| 实体 | 说明 |
| --- | --- |
| `LocalInstance` | 实例定义（**权威**）：名称、工作目录、`OwnedByServitium`、Java 绑定、启动配置、停止配置、自动启停、终端设置、RCON、查询设置、标签、备注 |
| `LocalInstanceRuntime` | 运行时状态：状态、进程标识、启动时间、重启计数 |
| `LocalJavaRuntime` | Java 运行时（**权威**）：路径、版本、架构、厂商、来源 |
| `LocalScheduledTask` | 计划任务（**权威**） |
| `LocalScheduledTaskRun` | 执行历史（**权威**，保留最近 100 条/任务） |
| `LocalFileTask` | 进行中的文件任务（用于并发限制与恢复） |
| `LocalUploadPart` | 上传分片状态 |
| `LocalOutboxEvent` | 待上报事件（控制平面离线期间累积） |
| `LocalConfigBackup` | 配置备份索引 |

### 4.2 离线累积

控制平面离线期间，Agent 把状态变化与执行历史写入 `LocalOutboxEvent`。重连后按序上报并清理。Outbox 上限 10000 条，超出时丢弃最旧的非关键事件（指标采样优先丢弃，状态变化与任务执行结果保留）。

## 5. HMCL 内容库

### 5.1 布局

```text
content\sha1\<哈希前2位>\<完整40位哈希>
```

文件无扩展名，内容即原始字节。

### 5.2 写入

1. 上传或从 Agent 传入的文件先落到 `staging\`；
2. 计算 SHA-1；
3. 若目标路径已存在且长度一致，复用（不重写）；
4. 否则原子移动到目标路径；
5. `HmclContentObject` 的引用计数在 Release 创建事务中增加。

### 5.3 回收

后台任务扫描 `ReferenceCount = 0` 且创建时间早于 24 小时的对象并删除。回收在 Channel 指针切换事务之外执行，且删除前二次确认引用计数仍为 0。

### 5.4 完整性巡检

见 `../design/mechanism-hmcl_distribution.md` 第 12 节。

## 6. 备份与恢复

### 6.1 数据库备份

| 场景 | 行为 |
| --- | --- |
| 迁移前 | 自动备份到 `backups\` |
| 定期 | 可配置的每日备份（默认开启，保留 7 份） |
| 手工 | 系统设置中提供「立即备份」 |

备份使用 SQLite 的在线备份 API，不停机。

### 6.2 恢复

恢复是运维操作，不在产品界面中提供：停止服务 → 替换数据库文件 → 启动服务。文档中给出步骤。

### 6.3 内容库与归档

内容库与归档不在数据库备份范围内。文档中说明完整备份需同时复制 `content\` 与 `archives\`，并强调二者与数据库必须来自同一时刻。

## 7. 保留期

| 数据 | 默认保留 | 可配置范围 |
| --- | --- | --- |
| 操作日志 | 180 天 | 30–3650 天 |
| 安全审计日志 | 365 天 | **不低于 90 天** |
| 节点指标采样 | 24 小时 | 1–168 小时 |
| 长操作记录 | 30 天 | 7–365 天 |
| 计划任务执行历史 | 每任务 100 条 | 10–1000 条 |
| 配置备份 | 每文件 10 份 | 1–50 份 |
| HMCL Release | 每 Channel 10 个 | 最少 3 个 |
| HMCL 访问统计 | 90 天 | 30–730 天 |
| 上传会话暂存 | 24 小时 | 1–168 小时 |
| 服务日志文件 | 30 天 | 7–365 天 |

清理由后台作业执行，每次清理写入操作日志。

## 8. 数据脱敏

存储层面：

| 数据 | 存储形式 |
| --- | --- |
| 用户密码 | Argon2id 哈希（含参数与盐） |
| TOTP 密钥 | DPAPI 加密（机器范围） |
| TOTP 恢复码 | 逐个哈希 |
| API Key | SHA-256 哈希 |
| 节点凭据 | 控制平面存哈希；Agent 侧 DPAPI 加密 |
| 节点注册令牌 | 哈希 |
| HMCL Channel 令牌 | SHA-256 哈希 |
| RCON 密码 | Agent 侧 DPAPI 加密；控制平面不存 |
| OIDC Client Secret | DPAPI 加密 |
| TLS PFX 密码 | DPAPI 加密 |
| Windows 运行账户密码 | Agent 侧 DPAPI 加密 |
| 会话令牌 | 存哈希 |

DPAPI 使用机器范围保护，意味着数据目录不能直接搬到另一台机器使用。该限制在文档中明示。

## 9. 大小估算

在容量目标（8 节点、64 实例）下：

| 项 | 估算 |
| --- | --- |
| 控制平面数据库 | < 200 MiB（含 180 天操作日志与 365 天审计） |
| 节点指标采样 | < 20 MiB |
| HMCL 内容库 | 取决于整合内容大小；10 个 Release 的增量存储约为单个 Release 的 1.5–3 倍 |
| Agent 本地数据库 | < 50 MiB/节点 |

数据库大小在系统信息中展示（`REQ-USR-024`）。
