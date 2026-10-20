# 协议与接口

本文件定义 Servitium 的全部对外与内部接口契约。实现阶段以本文件为准，不得自行发明端点、事件名或错误码。

## 1. 接口总表

| 接口 | 传输 | 用途 | 使用方 |
| --- | --- | --- | --- |
| `/api/v1/*` | HTTPS REST | 全部管理操作 | Web、Desktop、API Key 集成 |
| `/hubs/v1/events` | SignalR | 状态变化、进度、告警 | Web、Desktop |
| `/hubs/v1/terminal` | SignalR | 终端输入输出 | Web、Desktop |
| `/api/v1/instances/{id}/files/**` | HTTPS 流 | 上传分片与下载流 | Web、Desktop |
| `/hmcl/**` | HTTP GET/HEAD | HMCL 客户端分发 | HMCL 客户端 |
| `AgentControl.v1` | gRPC 双向流 | 控制平面 ↔ Agent | Agent |
| 本机换票管道 | 命名管道 | Desktop 免登 | Desktop |
| `/swagger/v1/swagger.json` | HTTP | OpenAPI 描述 | 代码生成与工具 |
| `/health/live`、`/health/ready` | HTTP | 健康检查 | 运维 |

## 2. 通用约定

### 2.1 版本

REST 路径中的 `v1` 是**主版本**。破坏性变更提升主版本并保留旧版本至少一个发布周期。非破坏性新增（新字段、新端点、新枚举值）在 `v1` 内进行；客户端必须容忍未知字段与未知枚举值。

SignalR Hub 路径同样带版本。gRPC 服务名带版本（`servitium.agent.v1.AgentControl`）。

### 2.2 标识与时间

* 所有标识为 `Guid`，序列化为小写带连字符形式；
* 所有时间为 `DateTimeOffset`，序列化为 ISO 8601 且偏移量恒为 `+00:00`；
* 时长以秒（整数）表示，字段名以 `Seconds` 结尾；
* 字节大小以 `long` 表示，字段名以 `Bytes` 结尾。

### 2.3 认证

| 方式 | 适用 |
| --- | --- |
| 会话 Cookie（`Secure`、`HttpOnly`、`SameSite=Strict`）+ 双提交 CSRF 令牌 | Web、Desktop 的 REST 与 SignalR |
| `Authorization: Bearer <apiKey>` | REST 集成；**不可用于 SignalR** |
| 无 | `/hmcl/**` 的公开 Channel；登录相关端点；健康检查 |

CSRF 令牌通过 `GET /api/v1/auth/csrf` 获取，放入 `X-Servitium-Csrf` 请求头。所有非幂等且使用 Cookie 认证的请求必须携带。

### 2.4 错误模型

RFC 7807 Problem Details，`Content-Type: application/problem+json`：

```json
{
  "type": "https://servitium.invalid/errors/instance-not-stopped",
  "title": "Instance is not stopped",
  "status": 409,
  "detail": "The instance must be stopped before its working directory can be deleted.",
  "instance": "/api/v1/instances/8f1c.../working-directory",
  "code": "instance-not-stopped",
  "traceId": "00-...",
  "errors": { "path": ["Path escapes the instance working directory."] }
}
```

* `code` 是**稳定错误码**（kebab-case），客户端按它分支与本地化；
* `detail` 固定为 `en-US`，仅供诊断，客户端不直接展示；
* `errors` 仅在校验失败（`422`）时出现，键为字段路径。

### 2.5 稳定错误码

| 错误码 | HTTP | 含义 |
| --- | --- | --- |
| `unauthenticated` | 401 | 未认证或会话失效 |
| `csrf-token-missing` | 403 | 缺少或无效的 CSRF 令牌 |
| `forbidden` | 403 | 已认证但无权限 |
| `account-disabled` | 403 | 账号已禁用 |
| `api-key-disabled` | 403 | API Key 功能被全局关闭 |
| `not-found` | 404 | 资源不存在 |
| `conflict` | 409 | 与当前状态冲突 |
| `already-initialized` | 409 | 系统已初始化 |
| `validation-failed` | 422 | 请求体校验失败 |
| `rate-limited` | 429 | 触发限流 |
| `internal-error` | 500 | 未预期错误 |
| `node-offline` | 503 | 目标节点不在线 |
| `node-incompatible` | 503 | 节点协议版本不兼容 |
| `agent-timeout` | 504 | Agent 指令超时 |
| `owner-immutable` | 409 | Owner 不可删除、降级或禁用 |
| `user-name-taken` | 409 | 用户名已被占用 |
| `weak-password` | 422 | 密码不满足强度要求 |
| `totp-required` | 401 | 需要提供 TOTP 验证码 |
| `totp-invalid` | 401 | TOTP 验证码无效 |
| `oidc-state-invalid` | 400 | OIDC `state` 无效或已消费 |
| `oidc-nonce-mismatch` | 400 | OIDC `nonce` 不匹配 |
| `oidc-subject-conflict` | 409 | 外部身份已绑定其他用户 |
| `oidc-discovery-failed` | 422 | 无法拉取 OIDC 发现文档 |
| `registration-token-invalid` | 400 | 注册令牌无效、过期或已使用 |
| `node-has-instances` | 409 | 节点上仍有已登记实例 |
| `java-runtime-invalid` | 422 | Java 可执行文件不可用或架构不支持 |
| `java-runtime-in-use` | 409 | Java 运行时仍被实例使用 |
| `java-runtime-not-imported` | 409 | 非导入型运行时不可删除文件 |
| `instance-not-stopped` | 409 | 实例未处于停止状态 |
| `instance-not-owned` | 409 | 外部工作目录不允许删除 |
| `instance-path-invalid` | 422 | 工作目录路径不合法 |
| `instance-path-conflict` | 409 | 工作目录与其他实例冲突 |
| `eula-not-accepted` | 409 | EULA 未同意 |
| `raw-command-disabled` | 403 | 高级原始命令模式未启用 |
| `raw-command-invalid` | 422 | 原始命令首参数不是已登记的 Java 可执行文件 |
| `path-escapes-root` | 422 | 路径逃逸出允许范围 |
| `path-name-invalid` | 422 | 文件名不合法 |
| `path-reparse-point` | 422 | 路径包含重解析点 |
| `file-locked` | 409 | 文件被运行中的实例占用 |
| `file-too-large` | 413 | 超出编辑或上传大小上限 |
| `disk-space-insufficient` | 507 | 目标卷可用空间不足 |
| `upload-session-expired` | 410 | 上传会话已过期 |
| `upload-part-checksum-mismatch` | 422 | 分片校验和不匹配 |
| `archive-unsafe` | 422 | 归档未通过安全检查 |
| `archive-too-large` | 413 | 归档解压后超出上限 |
| `config-parse-failed` | 422 | 配置文件解析失败 |
| `config-format-unsupported` | 422 | 不支持的配置格式 |
| `schedule-limit-reached` | 409 | 计划任务数量达到上限 |
| `schedule-name-taken` | 409 | 任务名重复 |
| `url-not-allowed` | 422 | URL 目标地址不被允许（SSRF 防护） |
| `redirect-limit-exceeded` | 422 | 重定向次数超限 |
| `hmcl-channel-slug-taken` | 409 | Channel slug 已存在 |
| `hmcl-release-validation-failed` | 422 | Release 门禁未通过 |
| `hmcl-content-missing` | 500 | 内容库中缺少 manifest 声明的文件 |
| `hmcl-no-active-release` | 404 | Channel 无生效的 Release |
| `hmcl-token-invalid` | 403 | Channel 访问令牌无效 |
| `tls-required` | 422 | 非回环监听需要 TLS 或可信代理模式 |
| `certificate-invalid` | 422 | 证书不可加载、已过期或私钥不可用 |
| `operation-not-cancellable` | 409 | 长操作已越过提交点或已进入终态，不能再取消 |
| `idempotency-conflict` | 409 | 相同幂等键的请求体不同 |

新增错误码必须同时更新本表、`Servitium.Contracts` 中的常量、以及两种语言的文案资源。

### 2.6 幂等

创建型与状态变更型 POST 接受 `Idempotency-Key` 请求头（客户端生成的 `Guid`）。服务端在 24 小时内缓存 `(用户, 端点, 幂等键)` → 首次响应。相同键但请求体不同时返回 `idempotency-conflict`。

### 2.7 分页

列表接口：

```text
GET /api/v1/instances?cursor=<opaque>&limit=50&...
```

响应：

```json
{ "items": [...], "nextCursor": "..." }
```

`nextCursor` 为 `null` 表示到达末尾。`limit` 默认 50、上限 200。游标不透明，客户端不解析。

### 2.8 长操作

可能超过 5 秒的动作返回：

```text
HTTP/1.1 202 Accepted
Location: /api/v1/operations/{operationId}
```

```json
{ "operationId": "...", "type": "instance.create", "status": "running" }
```

`Operation` 结构：

```json
{
  "operationId": "...",
  "type": "instance.create",
  "status": "pending|running|succeeded|failed|cancelled",
  "progress": 0.42,
  "stage": "extracting-archive",
  "startedAt": "...",
  "completedAt": null,
  "targetType": "instance",
  "targetId": "...",
  "initiatedBy": "...",
  "error": { "code": "...", "detail": "..." },
  "result": { }
}
```

取消：`POST /api/v1/operations/{id}/cancel`。操作越过其明确提交点或已进入终态后返回 `operation-not-cancellable`；提交点前必须接受取消并清理暂存。

## 3. REST 资源

以下按资源列出职责、主要操作、所需权限与长操作标记。省略了每个端点的完整字段定义——字段定义在 OpenAPI 中生成并作为唯一事实来源。

图例：`O` = Owner、`A` = Administrator、`U` = User（需实例授权）。

### 3.1 `auth`

| 方法与路径 | 说明 | 权限 |
| --- | --- | --- |
| `GET /api/v1/auth/bootstrap` | 是否已初始化、OIDC 状态、登录页文本 | 匿名 |
| `POST /api/v1/auth/bootstrap` | 首次初始化，创建 Owner | 匿名（受回环/令牌限制） |
| `GET /api/v1/auth/csrf` | 获取 CSRF 令牌 | 匿名 |
| `POST /api/v1/auth/login` | 用户名密码登录 | 匿名 |
| `POST /api/v1/auth/login/totp` | 提交 TOTP 或恢复码 | 待验证会话 |
| `POST /api/v1/auth/logout` | 登出 | 已认证 |
| `GET /api/v1/auth/me` | 当前用户与权限摘要 | 已认证 |
| `PATCH /api/v1/auth/me` | 修改显示名与语言 | 已认证 |
| `POST /api/v1/auth/me/password` | 修改密码 | 已认证 |
| `GET /api/v1/auth/me/sessions` | 我的会话列表 | 已认证 |
| `DELETE /api/v1/auth/me/sessions/{id}` | 撤销我的某个会话 | 已认证 |
| `POST /api/v1/auth/me/totp` | 开始绑定 TOTP | 已认证 |
| `POST /api/v1/auth/me/totp/confirm` | 确认绑定并取回恢复码 | 已认证 |
| `DELETE /api/v1/auth/me/totp` | 解绑 TOTP | 已认证 |
| `GET /api/v1/auth/me/api-keys` | 我的 API Key 列表 | 已认证 |
| `POST /api/v1/auth/me/api-keys` | 创建（明文仅此一次返回） | 已认证 |
| `DELETE /api/v1/auth/me/api-keys/{id}` | 吊销 | 已认证 |
| `GET /api/v1/auth/oidc/authorize` | 发起 OIDC 授权 | 匿名 |
| `GET /api/v1/auth/oidc/callback` | OIDC 回调 | 匿名 |
| `POST /api/v1/auth/oidc/bind` | 绑定外部身份到当前账号 | 已认证 |
| `DELETE /api/v1/auth/me/external-identity` | 解绑外部身份 | 已认证 |

### 3.2 `users`

| 方法与路径 | 说明 | 权限 | 长操作 |
| --- | --- | --- | --- |
| `GET /api/v1/users` | 列表（过滤、分页） | O/A | |
| `POST /api/v1/users` | 创建 | O/A | |
| `GET /api/v1/users/{id}` | 详情 | O/A | |
| `PATCH /api/v1/users/{id}` | 修改显示名、角色 | O/A | |
| `POST /api/v1/users/{id}/password` | 重置密码 | O/A | |
| `POST /api/v1/users/{id}/disable` | 禁用 | O/A | |
| `POST /api/v1/users/{id}/enable` | 启用 | O/A | |
| `DELETE /api/v1/users/{id}` | 删除 | O/A | |
| `DELETE /api/v1/users/{id}/totp` | 重置该用户的 TOTP | O/A | |
| `DELETE /api/v1/users/{id}/sessions` | 吊销该用户全部会话 | O/A | |
| `DELETE /api/v1/users/{id}/external-identity` | 解绑外部身份 | O/A | |
| `GET /api/v1/users/{id}/grants` | 该用户的实例授权 | O/A | |
| `PUT /api/v1/users/{id}/grants` | 全量设置授权 | O/A | |
| `POST /api/v1/users/{id}/grants` | 增加授权 | O/A | |
| `DELETE /api/v1/users/{id}/grants/{instanceId}` | 撤销授权 | O/A | |
| `GET /api/v1/users/{id}/windows-identity` | Windows SID 绑定 | O/A | |
| `PUT /api/v1/users/{id}/windows-identity` | 设置绑定 | O/A | |
| `GET /api/v1/users/overview` | 用户统计 | O/A | |

### 3.3 `nodes`

| 方法与路径 | 说明 | 权限 | 长操作 |
| --- | --- | --- | --- |
| `GET /api/v1/nodes` | 列表 | O/A | |
| `GET /api/v1/nodes/{id}` | 详情 | O/A | |
| `PATCH /api/v1/nodes/{id}` | 修改属性与节点级配置 | O/A | |
| `DELETE /api/v1/nodes/{id}` | 移除 | O/A | |
| `POST /api/v1/nodes/registration-tokens` | 生成注册令牌 | O/A | |
| `GET /api/v1/nodes/registration-tokens` | 未消费令牌列表 | O/A | |
| `DELETE /api/v1/nodes/registration-tokens/{id}` | 作废令牌 | O/A | |
| `POST /api/v1/nodes/{id}/reconnect` | 强制重连 | O/A | |
| `POST /api/v1/nodes/{id}/credential/rotate` | 轮换凭据 | O/A | |
| `POST /api/v1/nodes/{id}/credential/revoke` | 吊销凭据 | O/A | |
| `GET /api/v1/nodes/{id}/metrics` | 指标时间序列 | O/A | |

### 3.4 `java-runtimes`

| 方法与路径 | 说明 | 权限 | 长操作 |
| --- | --- | --- | --- |
| `GET /api/v1/nodes/{nodeId}/java-runtimes` | 列表 | O/A | |
| `POST /api/v1/nodes/{nodeId}/java-runtimes/discover` | 触发系统探测，返回候选 | O/A | ✔ |
| `POST /api/v1/nodes/{nodeId}/java-runtimes` | 登记（探测结果或手工路径） | O/A | |
| `POST /api/v1/nodes/{nodeId}/java-runtimes/import` | 导入压缩包（配合上传会话） | O/A | ✔ |
| `GET /api/v1/nodes/{nodeId}/java-runtimes/{id}` | 详情（含使用中的实例） | O/A | |
| `DELETE /api/v1/nodes/{nodeId}/java-runtimes/{id}` | 取消登记，`?deleteFiles=true` 仅对导入型有效 | O/A | ✔ |

### 3.5 `instances`

| 方法与路径 | 说明 | 权限 | 长操作 |
| --- | --- | --- | --- |
| `GET /api/v1/instances` | 列表（按节点、状态、名称、标签过滤） | O/A；U 仅见授权实例 | |
| `GET /api/v1/instances/grouped` | 全节点聚合视图 | O/A | |
| `POST /api/v1/instances` | 创建（空 / 附加目录） | O/A | ✔ |
| `POST /api/v1/instances/from-archive` | 从已上传归档创建 | O/A | ✔ |
| `GET /api/v1/instances/{id}` | 详情 | O/A/U | |
| `PATCH /api/v1/instances/{id}` | 修改配置 | O/A；U 限非危险项 | |
| `DELETE /api/v1/instances/{id}` | 删除登记（不触碰文件） | O/A | |
| `DELETE /api/v1/instances/{id}/working-directory` | 删除工作目录（仅 `OwnedByServitium`） | O/A | ✔ |
| `POST /api/v1/instances/{id}/start` | 启动 | O/A/U | ✔ |
| `POST /api/v1/instances/{id}/stop` | 停止 | O/A/U | ✔ |
| `POST /api/v1/instances/{id}/restart` | 重启 | O/A/U | ✔ |
| `POST /api/v1/instances/{id}/force-stop` | 强制终止 | O/A/U | ✔ |
| `POST /api/v1/instances/{id}/update` | 执行更新动作 | O/A | ✔ |
| `POST /api/v1/instances/{id}/eula/accept` | 同意 EULA | O/A/U | |
| `POST /api/v1/instances/{id}/command` | 发送控制台命令 | O/A/U | |
| `GET /api/v1/instances/{id}/status` | 运行时状态与资源占用 | O/A/U | |
| `GET /api/v1/instances/{id}/players` | 服务器查询与 RCON 玩家列表 | O/A/U | |
| `POST /api/v1/instances/{id}/rcon/command` | 通过 RCON 执行命令 | O/A/U | |
| `GET /api/v1/instances/{id}/logs` | 读取日志文件（按行范围、关键字） | O/A/U | |
| `POST /api/v1/instances/batch/{action}` | 批量 start/stop/restart/force-stop/unregister | O/A | ✔ |

### 3.6 `files`

全部路径前缀 `/api/v1/instances/{id}/files`。权限：O/A/U（受 `REQ-FIL-063` 全局开关约束）。

| 方法与路径 | 说明 | 长操作 |
| --- | --- | --- |
| `GET /` | 目录列表（`?path=&search=&sort=&cursor=`） | |
| `GET /status` | 卷容量与任务配额 | |
| `POST /directories` | 新建目录 | |
| `POST /files` | 新建空文件 | |
| `POST /rename` | 重命名 | |
| `POST /copy` | 复制 | ✔ |
| `POST /move` | 移动 | ✔ |
| `POST /delete` | 删除（批量） | ✔ |
| `POST /compress` | 压缩为 ZIP | ✔ |
| `POST /extract` | 解压 | ✔ |
| `GET /content` | 下载（支持 `Range`） | |
| `GET /text` | 读取文本（含编码协商） | |
| `PUT /text` | 保存文本（原子替换） | |
| `POST /download-from-url` | URL 下载 | ✔ |
| `POST /uploads` | 创建上传会话 | |
| `GET /uploads/{uploadId}` | 会话状态与已收分片位图 | |
| `PUT /uploads/{uploadId}/parts/{index}` | 上传分片 | |
| `POST /uploads/{uploadId}/complete` | 合并 | ✔ |
| `DELETE /uploads/{uploadId}` | 取消 | |

### 3.7 `configurations`

前缀 `/api/v1/instances/{id}/configurations`。

| 方法与路径 | 说明 |
| --- | --- |
| `GET /` | 识别到的配置文件列表（含格式、冲突标记、最后修改） |
| `GET /{configKey}` | 结构化读取（键值 + 字段定义 + 原始文本） |
| `PUT /{configKey}` | 结构化写入（无损、校验、备份、原子） |
| `GET /{configKey}/backups` | 备份列表 |
| `POST /{configKey}/backups/{backupId}/restore` | 从备份恢复 |
| `GET /schema/{configKey}` | 字段定义（用于表单渲染） |

### 3.8 `mods`

前缀 `/api/v1/instances/{id}/mods`。

| 方法与路径 | 说明 | 长操作 |
| --- | --- | --- |
| `GET /` | 列表（`?kind=mods|plugins`） | |
| `GET /{modId}` | 元数据详情 | |
| `POST /{modId}/enable` | 启用 | |
| `POST /{modId}/disable` | 停用 | |
| `DELETE /{modId}` | 删除 | |
| `GET /{modId}/config-files` | 关联配置文件 | |
| `POST /upload` | 上传 JAR（配合上传会话） | ✔ |

### 3.9 `schedules`

前缀 `/api/v1/instances/{id}/schedules`。

| 方法与路径 | 说明 |
| --- | --- |
| `GET /` | 列表 |
| `POST /` | 创建 |
| `GET /{scheduleId}` | 详情 |
| `PUT /{scheduleId}` | 修改 |
| `DELETE /{scheduleId}` | 删除 |
| `POST /{scheduleId}/enable` | 启用 |
| `POST /{scheduleId}/disable` | 停用 |
| `POST /{scheduleId}/run` | 立即执行一次 |
| `GET /{scheduleId}/runs` | 执行历史 |

### 3.10 `layouts`

| 方法与路径 | 说明 | 权限 |
| --- | --- | --- |
| `GET /api/v1/layouts/{page}` | 我的该页布局 | 已认证 |
| `PUT /api/v1/layouts/{page}` | 保存 | 已认证 |
| `DELETE /api/v1/layouts/{page}` | 重置为默认 | 已认证 |
| `GET /api/v1/layouts` | 我的全部页面布局 | 已认证 |

### 3.11 `operations`

| 方法与路径 | 说明 | 权限 |
| --- | --- | --- |
| `GET /api/v1/operations` | 列表（按状态、类型、目标、发起者过滤） | O/A；U 仅见自己发起的 |
| `GET /api/v1/operations/{id}` | 详情 | 同上 |
| `POST /api/v1/operations/{id}/cancel` | 取消 | 发起者或 O/A |

### 3.12 `audit`

| 方法与路径 | 说明 | 权限 |
| --- | --- | --- |
| `GET /api/v1/audit/activity` | 操作日志（过滤、分页） | O/A |
| `GET /api/v1/audit/activity/export` | 导出 CSV | O/A |
| `GET /api/v1/audit/security` | 安全审计日志 | O/A |
| `GET /api/v1/audit/security/export` | 导出 CSV | O/A |

无任何写入、修改或删除审计日志的端点。

### 3.13 `hmcl`

| 方法与路径 | 说明 | 权限 | 长操作 |
| --- | --- | --- | --- |
| `GET /api/v1/hmcl/channels` | Channel 列表 | O/A | |
| `POST /api/v1/hmcl/channels` | 创建 | O/A | |
| `GET /api/v1/hmcl/channels/{id}` | 详情（含分发链接与统计） | O/A | |
| `PATCH /api/v1/hmcl/channels/{id}` | 修改显示名、描述、保留数、关联实例 | O/A | |
| `DELETE /api/v1/hmcl/channels/{id}` | 删除 | O/A | ✔ |
| `POST /api/v1/hmcl/channels/{id}/visibility` | 切换可见性 | O/A | |
| `POST /api/v1/hmcl/channels/{id}/token/rotate` | 轮换令牌 | O/A | |
| `GET /api/v1/hmcl/channels/{id}/releases` | Release 列表 | O/A | |
| `POST /api/v1/hmcl/channels/{id}/releases` | 从归档或实例创建 Release | O/A | ✔ |
| `GET /api/v1/hmcl/releases/{id}` | Release 详情 | O/A | |
| `GET /api/v1/hmcl/releases/{id}/files` | 文件清单（分页、搜索） | O/A | |
| `GET /api/v1/hmcl/releases/{id}/manifest` | manifest 预览（只读） | O/A | |
| `POST /api/v1/hmcl/channels/{id}/publish` | 发布或回滚到指定 Release | O/A | ✔ |
| `POST /api/v1/hmcl/channels/{id}/preview` | 差异预览 | O/A | |
| `GET /api/v1/hmcl/channels/{id}/stats` | 访问统计 | O/A | |
| `POST /api/v1/hmcl/archives` | 创建归档上传会话 | O/A | |
| `DELETE /api/v1/hmcl/archives/{id}` | 删除归档 | O/A | |

### 3.14 `settings`

| 方法与路径 | 说明 | 权限 |
| --- | --- | --- |
| `GET /api/v1/settings` | 全部设置（秘密以占位符返回） | O/A |
| `PATCH /api/v1/settings/{group}` | 按分组保存（`general`/`appearance`/`security`/`identity`） | O/A |
| `POST /api/v1/settings/assets` | 上传 Logo/背景/图标 | O/A |
| `DELETE /api/v1/settings/assets/{kind}` | 清除资源 | O/A |
| `POST /api/v1/settings/identity/test` | 测试 OIDC 发现文档 | O/A |
| `POST /api/v1/settings/tls/test` | 校验证书 | O/A |
| `GET /api/v1/settings/system-info` | 系统信息 | O/A |
| `POST /api/v1/settings/diagnostics` | 生成诊断导出 | O/A |
| `POST /api/v1/settings/database/backup` | 立即备份数据库 | O |

### 3.15 `ip-bans`

| 方法与路径 | 说明 | 权限 |
| --- | --- | --- |
| `GET /api/v1/ip-bans` | 当前临时封禁列表 | O/A |
| `DELETE /api/v1/ip-bans/{id}` | 解除封禁 | O/A |

## 4. `/hubs/v1/events`

### 4.1 连接与订阅

客户端连接后调用：

```text
Subscribe(scope)     scope 形如 "global" | "node:{id}" | "instance:{id}" | "operation:{id}"
Unsubscribe(scope)
Resume(lastEventId)  请求补发最近事件（上限 200 条）
```

服务端在每次订阅与每次推送前执行授权校验。无权限的订阅返回错误且不建立。

### 4.2 事件信封

```json
{
  "eventId": 128374,
  "type": "instance.status-changed",
  "occurredAt": "2026-08-02T12:00:00+00:00",
  "scope": "instance:8f1c...",
  "payload": { }
}
```

`eventId` 为服务端单调递增的整数，用于 `Resume`。

### 4.3 事件类型

| 类型 | 作用域 | payload 要点 |
| --- | --- | --- |
| `node.status-changed` | global, node | 节点标识、新状态、原因 |
| `node.metrics` | node | CPU、内存、磁盘、采样时间 |
| `node.credential-rotated` | global | 节点标识 |
| `instance.created` | global, node | 实例摘要 |
| `instance.updated` | instance | 变更字段集合 |
| `instance.unregistered` | global, node | 实例标识 |
| `instance.status-changed` | instance, node, global | 旧状态、新状态、原因、退出码 |
| `instance.resource-usage` | instance | CPU、内存、玩家数 |
| `instance.crashed` | instance, global | 退出码、最近输出片段 |
| `instance.auto-restart-exhausted` | instance, global | 已重启次数 |
| `instance.players-changed` | instance | 在线数、最大数 |
| `operation.started` | operation, global | 类型、目标 |
| `operation.progress` | operation | 进度、阶段 |
| `operation.completed` | operation, global | 状态、结果或错误 |
| `file.task-progress` | instance | 任务类型、进度 |
| `schedule.run-completed` | instance | 任务标识、结果 |
| `hmcl.release-created` | global | Channel、Release |
| `hmcl.published` | global | Channel、Release、差异统计 |
| `hmcl.integrity-alert` | global | Channel、Release、缺失条目数 |
| `audit.security-event` | global（仅 O/A） | 事件类别摘要 |
| `session.revoked` | 定向到该用户 | 原因 |
| `authorization.changed` | 定向到该用户 | 需重新拉取权限 |

### 4.4 授权变更的即时生效

服务端在授权变更后向受影响的连接推送 `authorization.changed` 并主动关闭其不再被允许的订阅（含终端）。客户端收到后重新拉取 `GET /api/v1/auth/me` 并刷新界面。

## 5. `/hubs/v1/terminal`

### 5.1 方法

| 方向 | 方法 | 说明 |
| --- | --- | --- |
| 客户端 → 服务端 | `Attach(instanceId)` | 订阅该实例终端；返回缓冲快照的元信息 |
| 客户端 → 服务端 | `Detach(instanceId)` | 取消订阅 |
| 客户端 → 服务端 | `SendInput(instanceId, text)` | 发送输入（服务端按实例配置追加换行） |
| 客户端 → 服务端 | `Resize(instanceId, columns, rows)` | 调整尺寸 |
| 服务端 → 客户端 | `Buffer(instanceId, chunks)` | Attach 后的历史缓冲 |
| 服务端 → 客户端 | `Output(instanceId, chunk)` | 实时输出 |
| 服务端 → 客户端 | `Watchers(instanceId, count)` | 观察者数量变化 |
| 服务端 → 客户端 | `Closed(instanceId, reason)` | 订阅被关闭（实例停止、权限撤销、节点离线） |

### 5.2 数据形态

输出块为 UTF-8 字符串（Agent 已按实例输出编码解码）。保留 ANSI 转义序列；是否渲染由客户端按实例设置决定。

### 5.3 背压

服务端为每个连接维护发送队列，上限 4 MiB。超出时丢弃最旧的输出块并插入一条标记块告知客户端「部分输出被丢弃」。

## 6. `AgentControl.v1` gRPC

### 6.1 服务定义

```protobuf
service AgentControl {
  // Agent 主动建立的唯一双向流，承载握手、指令、事件与所有子流
  rpc Connect(stream AgentMessage) returns (stream ServerMessage);
}
```

单条双向流承载全部通信，通过 `StreamId` 复用。理由：避免 Agent 需要维持多条连接，简化 NAT 与代理场景。

### 6.2 消息结构

```protobuf
message AgentMessage {
  oneof body {
    Hello hello = 1;                    // 握手
    CommandResponse command_response = 2;
    Event event = 3;
    StreamChunk stream_chunk = 4;       // 终端输出、文件下载数据
    StreamControl stream_control = 5;   // 打开/关闭/背压
    Heartbeat heartbeat = 6;
  }
}

message ServerMessage {
  oneof body {
    Welcome welcome = 1;
    CommandRequest command_request = 2;
    StreamChunk stream_chunk = 3;       // 终端输入、文件上传数据
    StreamControl stream_control = 4;
    HeartbeatAck heartbeat_ack = 5;
  }
}
```

### 6.3 握手

`Hello` 携带：节点凭据（或注册令牌）、Agent 版本、协议主次版本、平台信息、机器指纹。

`Welcome` 返回：节点标识、协商后的协议版本、服务端时间、节点级配置快照。

协议主版本不一致时服务端返回错误并关闭流（`node-incompatible`）。

### 6.4 指令

`CommandRequest` 携带 `commandId`、类型与载荷（按类型的 protobuf message）；`CommandResponse` 携带 `commandId`、成功标记、载荷或错误。服务端对每条指令设置超时（默认 30 秒，长操作类指令使用 `Ack + 事件` 模式而非等待完成）。

指令类型（分组）：

| 组 | 指令 |
| --- | --- |
| 实例 | `CreateInstance`、`UpdateInstance`、`UnregisterInstance`、`DeleteWorkingDirectory`、`StartInstance`、`StopInstance`、`RestartInstance`、`ForceStopInstance`、`SendCommand`、`AcceptEula`、`GetInstanceStatus`、`QueryPlayers`、`RconCommand`、`RunUpdateAction` |
| Java | `DiscoverJava`、`RegisterJava`、`ImportJava`、`UnregisterJava` |
| 文件 | `ListDirectory`、`GetFileSystemStatus`、`CreateDirectory`、`CreateFile`、`Rename`、`Copy`、`Move`、`Delete`、`Compress`、`Extract`、`ReadText`、`WriteText`、`DownloadFromUrl`、`CancelFileTask` |
| 配置 | `ListConfigurations`、`ReadConfiguration`、`WriteConfiguration`、`ListConfigBackups`、`RestoreConfigBackup` |
| 模组 | `ListMods`、`GetModMetadata`、`ToggleMod`、`DeleteMod`、`ListModConfigFiles` |
| 计划任务 | `ListSchedules`、`UpsertSchedule`、`DeleteSchedule`、`SetScheduleEnabled`、`RunScheduleNow`、`ListScheduleRuns` |
| 节点 | `UpdateNodeConfig`、`GetMetrics`、`RotateCredential`、`Reconcile` |
| HMCL | `CollectDirectoryForRelease`、`SyncFromRelease` |

### 6.5 子流

`StreamControl` 用于打开与关闭子流：

| 子流类型 | 方向 | 用途 |
| --- | --- | --- |
| `terminal` | 双向 | 终端输入与输出 |
| `file-upload` | Server → Agent | 上传数据落盘 |
| `file-download` | Agent → Server | 下载数据读取 |
| `content-collect` | Agent → Server | 为 HMCL Release 收集实例目录内容 |
| `content-sync` | Server → Agent | 把 Release 内容同步到实例目录 |

每条子流有独立的 `StreamId`、序号与背压窗口。接收方通过 `StreamControl` 的 `window_update` 反馈可接收字节数。

### 6.6 事件

Agent 主动上报：实例状态变化、资源占用、崩溃、计划任务执行结果、指标采样、文件任务进度、长操作进度、日志片段。

### 6.7 心跳

Agent 每 10 秒发送 `Heartbeat`。服务端 35 秒未收到即判定离线并关闭流。Agent 侧 35 秒未收到 `HeartbeatAck` 即主动重连。

### 6.8 对账

连接建立后，服务端下发 `Reconcile` 指令，Agent 返回其本地的实例、Java 运行时、计划任务的完整快照与版本号。服务端以 Agent 为准更新镜像，差异写入操作日志。

## 7. 本机换票管道

| 项 | 规范 |
| --- | --- |
| 管道名 | `\\.\pipe\Servitium.LocalAuth.v1` |
| ACL | `SYSTEM` 与服务账户完全控制；`Authenticated Users` 仅连接与读写；`Everyone` 无权限 |
| 协议 | 单次请求-响应，JSON 行 |
| 请求 | `{ "action": "issue-session", "clientVersion": "..." }` |
| 服务端校验 | 读取对端进程 SID 与可执行文件完整路径；SID 必须绑定到未禁用的用户；路径必须位于安装目录 |
| 响应（成功） | `{ "sessionToken": "...", "expiresAt": "...", "user": { ... } }` |
| 响应（失败） | `{ "error": { "code": "...", "detail": "..." } }` |
| 令牌用途 | 换取标准会话 Cookie：`POST /api/v1/auth/local-session`，请求体携带该令牌 |
| 有效期 | 令牌 60 秒一次性；换得的会话默认 15 分钟并可续期 |

## 8. HMCL 分发端点

见 `../reference/protocol-hmcl_update.md` 第 8 节与 `../design/mechanism-hmcl_distribution.md` 第 5–6 节。要点：

| 端点 | 强制 |
| --- | --- |
| `GET /hmcl/{channel}[/{token}]/server-manifest.json` | `Cache-Control: no-cache`；强 ETag；支持 `304` |
| `GET\|HEAD /hmcl/{channel}[/{token}]/overrides/{path}` | 强 ETag = 文件 SHA-1；`Cache-Control: no-cache, no-transform`；支持 `Range`；恒不压缩 |

这两个端点**不参与**通用的 CSRF、CORS、会话与压缩中间件；它们有独立的中间件管线。

## 9. OpenAPI 与代码生成

| 产物 | 生成方式 |
| --- | --- |
| `swagger.json` | 由 `Servitium.Server` 在构建时生成并作为构建产物固化 |
| TypeScript 客户端 | 从 `swagger.json` 生成到 `web/src/api`，不手工编辑 |
| .NET 客户端 | `Servitium.Client` 手写薄封装 + 从 `Servitium.Contracts` 直接引用 DTO |
| protobuf C# | 从 `.proto` 文件生成 |

`dotnet test` 校验「生成产物与来源一致」：生成到独立临时目录后，与目标清单逐文件、逐字节比较，存在缺失、多余或内容差异即失败。

## 10. 兼容性规则

| 变更 | 允许 |
| --- | --- |
| 新增可选请求字段 | ✔ |
| 新增响应字段 | ✔ |
| 新增端点 | ✔ |
| 新增枚举值 | ✔（客户端必须容忍未知值） |
| 新增事件类型 | ✔ |
| 新增错误码 | ✔ |
| 修改字段类型 | ✘ |
| 删除字段 | ✘ |
| 修改字段语义 | ✘ |
| 修改稳定错误码字符串 | ✘ |
| 修改 gRPC 字段编号 | ✘ |

契约测试对上述规则做回归守护。
