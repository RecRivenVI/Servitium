# 阶段 12：Server、身份与授权

## 1. 阶段目的

让控制平面可以真正运行：持久化、认证、授权、会话、REST 骨架、实时事件、错误模型、限流与安全响应头。此后各阶段在此基础上增加业务端点。

## 2. 需求编号

`REQ-IDN-001` … `REQ-IDN-058`（全部）、`REQ-USR-001` … `REQ-USR-005`、`REQ-USR-010` … `REQ-USR-012`、`REQ-PLT-010`、`REQ-PLT-020`、`REQ-PLT-021`、`REQ-PLT-022`、`REQ-PLT-030`、`REQ-SEC-010`、`REQ-SEC-020`、`REQ-SEC-021`、`REQ-USR-054`（CORS）、`REQ-SEC-060`、`REQ-SEC-070`、`REQ-SEC-080`、`REQ-DSK-002`、`REQ-DSK-010`、`REQ-SEC-040`。

## 3. 前置条件

* 阶段 11 完成条件全部满足。

## 4. 范围

* `Servitium.Infrastructure` 的持久化实现：EF Core `DbContext`、映射、迁移、SQLite 配置；
* 密码哈希、TOTP、DPAPI、令牌生成的实现；
* 认证：会话 Cookie、CSRF、API Key、OIDC、本机换票管道；
* OIDC 使用的受 SSRF 约束出站 HTTP 客户端；
* 授权：策略接入 ASP.NET Core、四路径统一；
* REST 骨架：`auth`、`users`、`operations`、`audit`（读）、`settings`（基础与安全分组）、`ip-bans`；
* 实时事件：`/hubs/v1/events`；
* 长操作基础设施；
* 统一错误处理、限流、安全响应头、CORS、可信代理；
* 双日志流（操作日志与安全审计）。

## 5. 非目标

* 不实现 Agent 连接与 gRPC 网关（阶段 13）；
* 不实现实例、文件、配置、模组、计划任务、HMCL 端点；
* 不实现终端 Hub；
* 不实现 Web 与 Desktop。

## 6. 需要创建的项目或目录

```text
src/Servitium.Infrastructure/
├── Persistence/            DbContext、配置、迁移、仓储实现、单写入队列
├── Security/               Argon2id、TOTP、DPAPI、令牌生成、脱敏
├── Http/                   受 SSRF 约束的出站 HTTP 客户端
├── Time/                   IClock 实现
└── Interop/                DPAPI 与命名管道 ACL 的 Win32 封装

src/Servitium.Server/
├── Hosting/                宿主、配置、健康检查、Windows 服务
├── Security/               认证处理器、授权策略、CSRF、限流、可信代理、安全响应头、本机管道服务
├── Endpoints/              auth、users、operations、audit、settings、ip-bans
├── Hubs/                   EventsHub
├── Operations/             长操作调度与持久化
├── Realtime/               事件扇出与作用域过滤
└── Diagnostics/            统一异常处理与 Problem Details
```

## 7. 需要实现的类型或模块

### 7.1 持久化

* `ServitiumDbContext` 与全部实体配置（见 `data-and-storage.md` 3.1、3.2）；
* 初始迁移；
* SQLite 配置（WAL、`synchronous=NORMAL`、外键、`busy_timeout`）；
* 单写入队列：所有写事务经一个串行通道执行；
* 迁移前自动备份。

### 7.2 安全实现

| 组件 | 说明 |
| --- | --- |
| `Argon2idPasswordHasher` | 参数编码进哈希串；支持参数升级时的透明重哈希 |
| `TotpService` | RFC 6238，SHA-1，6 位，30 秒；漂移容忍可配置 |
| `DpapiSecretProtector` | 机器范围 |
| `TokenGenerator` | `RandomNumberGenerator`，Base64URL |
| `SecretRedactor` | 统一脱敏；作用于日志、事件、响应、错误、诊断导出 |
| `RestrictedHttpClient` | 统一执行 URL 规范化、DNS 全量校验、绑定已校验 IP、逐跳重定向重校验、超时与响应大小限制；阶段 15 的 URL 下载复用 |

### 7.3 认证

| 方式 | 实现 |
| --- | --- |
| 会话 Cookie | 自定义认证处理器；`Secure`、`HttpOnly`、`SameSite=Strict`；会话记录持久化；登录后重新生成标识 |
| CSRF | 双提交令牌，`GET /api/v1/auth/csrf` 下发；所有非幂等 Cookie 请求校验；SignalR 握手同样校验 |
| API Key | `Authorization: Bearer`；SHA-256 比对；作用域与过期校验；不可用于 SignalR |
| OIDC | 发现文档 + JWKS 缓存；`state`/`nonce` 存储与一次性消费；ID Token 全量校验；按 `(issuer, subject)` 绑定 |
| 本机换票 | 命名管道服务：ACL、对端 SID 与可执行路径校验、一次性令牌；`POST /api/v1/auth/local-session` 换取会话 |

### 7.4 授权

* `Servitium.Application` 的 `IAuthorizationService` 接入 ASP.NET Core 授权管线；
* 一个端点过滤器统一执行判定，端点通过属性声明所需资源与动作；
* 架构测试断言不存在绕过路径；
* 权限变更即时生效：变更后向受影响连接推送 `authorization.changed` 并关闭其订阅；取消其进行中的长操作。

### 7.5 REST 端点

按 `protocols-and-contracts.md` 3.1、3.2、3.11、3.12、3.14、3.15 实现。本阶段的 `settings` 仅实现「基础」与「安全」分组中与本阶段相关的项（监听、TLS、CORS、可信代理、限流、TOTP 漂移、API Key 开关、会话有效期）。

### 7.6 实时事件

* `EventsHub`：`Subscribe`、`Unsubscribe`、`Resume`；
* 事件存储：最近 200 条的环形缓冲用于 `Resume`；
* 作用域过滤与授权校验；
* 本阶段实现的事件：`session.revoked`、`authorization.changed`、`operation.*`、`audit.security-event`。

### 7.7 长操作

* `Operation` 持久化；
* `IOperationTracker` 实现：创建、进度、阶段、完成、失败、取消；
* Server 重启后把「进行中且执行体在 Server」的操作标记为中断。

### 7.8 中间件管线

顺序固定：

```text
异常处理 → 可信代理 → 安全响应头（CSP/HSTS/X-Frame-Options 等）
→ 路由 → CORS → 限流 → 认证 → CSRF → 授权 → 端点
```

`/hmcl/**` 使用独立管线（阶段 17），不经 CSRF、CORS 与会话认证。

## 8. 数据模型

本阶段落地 `data-and-storage.md` 3.1 中除 `Instance`、`JavaRuntime`、`ScheduledTask*`、`Hmcl*`、`UploadSession`、`NodeMetricSample` 之外的全部实体。其余在阶段 13–17 增量迁移。

## 9. API 或事件

见 7.5、7.6。全部端点必须出现在 OpenAPI 中且 `Generate.ps1` 能生成客户端。

## 10. 实施顺序

1. `Infrastructure/Persistence`：`DbContext`、实体配置、SQLite 配置、单写入队列；
2. 初始迁移 + 迁移前备份 + 启动时自动应用；
3. `Infrastructure/Security`：Argon2id、TOTP、DPAPI、令牌、脱敏；
4. `Infrastructure/Time`：`IClock`；
5. 仓储实现；
6. `Server/Diagnostics`：统一异常 → Problem Details；
7. `Server/Security`：安全响应头、可信代理、CORS、限流；
8. 会话认证 + CSRF；
9. `auth` 端点：bootstrap、登录、TOTP、登出、me、会话、API Key；
10. 授权管线接入 + 端点过滤器；
11. `users` 端点 + 授权管理；
12. 双日志流（操作日志、安全审计）与脱敏接入；
13. 长操作基础设施 + `operations` 端点；
14. `EventsHub` + 事件扇出 + `Resume`；
15. 权限变更即时生效；
16. 受 SSRF 约束的出站 HTTP 客户端与负向测试；
17. OIDC；
18. 本机换票管道 + `local-session` 端点；
19. `settings` 端点（基础与安全分组的本阶段项）；
20. `ip-bans` 端点；
21. OpenAPI 输出 + `Generate.ps1` 生成客户端；
22. 测试；
23. 运行 `eng/Validate.ps1`。

## 11. 关键算法

| 算法 | 说明 |
| --- | --- |
| 登录流程 | 查用户 → 校验密码（恒定时间比较）→ 若绑定 TOTP 则创建受限中间会话 → TOTP 校验 → 创建正式会话（新标识）→ 写审计 |
| 限流退避 | 同用户名连续失败 n 次后退避 `2^(n-5)` 分钟（上限 16）；同 IP 10 分钟内 20 次失败后封禁 30 分钟 |
| OIDC 回调 | 校验 `state` 存在且未消费 → 换取令牌 → 校验签名/`iss`/`aud`/`exp`/`iat`/`nonce` → 按 `(issuer, subject)` 查绑定 → 有绑定则登录，无绑定则进入绑定流程 |
| 本机换票 | 读取管道对端 PID → 打开进程令牌取 SID → 查 `WindowsIdentityBinding` → 校验对端可执行文件路径位于安装目录 → 生成 60 秒一次性令牌 |
| 权限变更生效 | 变更提交后，按用户查活动连接 → 推送 `authorization.changed` → 关闭不再允许的订阅 → 取消其进行中的长操作 → 断开使用中的文件流 |
| 事件 `Resume` | 客户端提供 `lastEventId`；服务端从环形缓冲取大于该值且客户端有权访问的事件，最多 200 条；超出缓冲范围时返回「需要全量刷新」标记 |

## 12. Windows 平台事项

* 命名管道 ACL 通过 Win32 互操作设置（`Interop/`）；
* 对端进程 SID 与可执行文件路径通过进程令牌与 `QueryFullProcessImageName` 获取；
* DPAPI 机器范围加密；
* Windows 服务宿主与优雅停止；
* TLS 证书可从 Windows 证书存储按指纹加载。

## 13. 安全要求

本阶段必须实现并测试的威胁应对（见 `security-model.md`）：

`THR-001`、`THR-002`、`THR-003`、`THR-004`、`THR-005`、`THR-006`、`THR-007`、`THR-010`、`THR-011`、`THR-012`、`THR-020`、`THR-021`、`THR-022`（CSP 部分）、`THR-023`、`THR-024`、`THR-040`（OIDC 入口）、`THR-080`、`THR-082`。

## 14. 自动化测试

单元测试新增：密码哈希参数、TOTP 校验与漂移、脱敏规则、限流退避计算、游标分页实现、事件作用域过滤。

架构测试新增：

| 编号 | 断言 |
| --- | --- |
| `ARC-030` | 所有 REST 端点都经过授权过滤器（无例外或例外在白名单中且白名单与文档一致） |
| `ARC-031` | 所有 Hub 方法都经过授权判定 |
| `ARC-032` | 数据访问层对审计表不产生 UPDATE/DELETE（通过拦截器断言） |
| `ARC-033` | 无端点关闭 CSRF 校验 |

## 15. 集成测试

`Servitium.Server.IntegrationTests`（分类 `Integration`），使用真实 SQLite 与内存主机：

| 编号族 | 内容 |
| --- | --- |
| `INT-100` | 迁移可在空库与含数据库上重复应用；迁移前备份产生 |
| `INT-110` | 首次初始化、二次初始化被拒、非回环被拒 |
| `INT-120` | 登录、TOTP、恢复码、登出、会话过期与滑动续期 |
| `INT-130` | API Key 创建、使用、吊销、总开关 |
| `INT-140` | 用户 CRUD、禁用、Owner 保护、级联清理 |
| `INT-150` | 授权矩阵：角色 × 端点的允许与拒绝 |
| `INT-160` | 权限变更即时生效（订阅关闭 ≤ 2 秒） |
| `INT-170` | 长操作创建、进度、完成、取消、重启后中断标记 |
| `INT-180` | 事件订阅、作用域过滤、`Resume` 补发 |
| `INT-190` | OIDC 全流程（本地 OIDC 桩）：授权、回调、绑定、解绑；所有出站端点经过受限客户端 |
| `INT-200` | 本机换票（在同机以测试进程模拟对端） |
| `INT-210` | 双日志流写入与查询；审计不可修改 |
| `INT-220` | 错误模型：每个已实现端点的错误码覆盖 |
| `INT-230` | 幂等键：重复提交不产生第二个实体 |
| `INT-240` | 游标分页在数据变动下不重不漏 |
| `INT-250` | 设置保存：非回环监听缺少 TLS 与代理模式时被拒 |

安全测试（分类 `Security`）：`SEC-001` … `SEC-012`、`SEC-020` … `SEC-032`、`SEC-040` … `SEC-044`、`SEC-050` … `SEC-052`、`SEC-060` … `SEC-062`、`SEC-070`、`SEC-071`、`SEC-080`、`SEC-081`、`SEC-090`、`SEC-100`、`SEC-110`、`SEC-120`、`SEC-130`、`SEC-131`、`SEC-190` … `SEC-193`（OIDC 入口）、`SEC-330`（本阶段已有秘密的部分）、`SEC-350`、`SEC-351`。

契约测试：OpenAPI 生成成功；生成的 TypeScript 客户端与 `swagger.json` 一致；错误码常量与文档一致。

## 16. 人工测试

无。本机换票的完整验证在阶段 22 与 24 补充（需要真实 Desktop 进程）。

## 17. 调试启动方式

```powershell
dotnet run --project src/Servitium.Server -- --environment Development
# 浏览器访问 https://127.0.0.1:5344/swagger 查看端点
./eng/Test.ps1 -Category Integration
./eng/Test.ps1 -Category Security
```

## 18. 验收责任方

编码 Agent 自主验收。

## 19. 完成条件

1. 控制平面可启动，完成初始化并登录；
2. `INT-100` … `INT-250` 全部通过；
3. 本阶段列出的全部 `SEC-*` 测试通过；
4. `ARC-030` … `ARC-033` 通过；
5. OpenAPI 可生成且客户端生成一致性校验通过；
6. 迁移在空库与含数据库上均可应用；
7. `eng/Validate.ps1` 全量通过；
8. 覆盖率门禁通过。

## 20. 禁止提前进入下一阶段的条件

* 授权未统一到单一服务或存在绕过路径；
* CSRF 可被关闭；
* 审计可被修改；
* 权限变更不能即时生效；
* 非回环监听可在无 TLS 与无可信代理模式下保存；
* 秘密出现在日志或响应中。

## 21. 需要更新的动态记录

* `docs/records/current-status.md`
* `docs/records/development-log.md`
* `docs/records/decision-log.md`
* `docs/records/problem-log.md`（如有）
* `docs/records/validation-log.md`
* `docs/testing/requirements-traceability.md`
* `docs/testing/acceptance-matrix.md`
