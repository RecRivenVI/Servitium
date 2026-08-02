# MCSManager 功能完整清单

证据快照：`MCSM@072fc59`，验证方式为 `[SOURCE]`。本清单记录该快照下已识别的功能集合，不是 Servitium 需求列表。

功能分布、权限、字段或行为需要版本复核时，按 `source-evidence-index.md` 前往登记路径取证。采用、适配与排除决定的变化按 `AGENTS.md` 处理。

## 1. 分类定义

| 分类 | 含义 |
| --- | --- |
| **采用** | 直接进入 Servitium，保持等价业务能力 |
| **适配** | 进入 Servitium，但按 Windows、Minecraft Java、服务化或 Fluent 2 调整 |
| **排除** | 明确不实现，附产品边界理由 |
| **不适用** | 因平台、产品范围或技术模型不同而不存在对应概念 |

Servitium 需求编号体系见 `docs/product/functional-specification.md`。

## 2. 角色模型对照

`[SOURCE]` `frontend/src/config/router.ts :: ROLE`、`panel/src/app/service/permission_service.ts`

| MCSManager | 数值 | 语义 | Servitium 对应 |
| --- | --- | --- | --- |
| `GUEST` | 0 | 未登录 | 匿名（仅登录与 HMCL 公开分发） |
| `USER` | 1 | 普通用户，仅能操作被显式授权的实例 | `User` |
| `ADMIN` | 10 | 管理员，全量权限 | `Administrator` |
| —（无） | — | 无所有者概念，首个安装用户即管理员 | `Owner`（Servitium 新增，唯一，不可删除） |
| —（无） | — | 无禁用态，只能删除用户 | `Disabled`（Servitium 新增） |

`[SOURCE]` 权限判定核心为两个函数：`isTopPermission(user)` 判 `permission >= 10`；`isHaveInstance(user, daemonId, instanceUuid)` 遍历用户的 `instances` 数组做 `(daemonId, instanceUuid)` 精确匹配。授权粒度为「用户 × 节点 × 实例」，无角色继承、无按操作细分。

Servitium 采纳同样的「实例级白名单」粒度（`REQ-IDN-030`），并新增 `Owner` 与 `Disabled`。

## 3. 前端路由与页面

`[SOURCE]` `frontend/src/config/router.ts`；页面内容由 `panel/src/app/service/frontend_layout.ts` 的默认布局决定。

MCSManager 的路由本身不承载内容：除 `/install`、`/login`、`/sso/bind` 外，所有路由都渲染同一个 `LayoutContainer`，由布局配置决定该路由显示哪些卡片组件。路由表共列出 **27 个路由条目**（其中包含嵌套子路由），默认布局含 **24 个页面条目**（含 `__settings__` 主题条目与 `/_open_page`）。

| # | 路由 | 名称语义 | 最低权限 | 主菜单 | 布局卡片 | Servitium 分类 |
| --- | --- | --- | --- | --- | --- | --- |
| R01 | `/install` | 首次初始化 | GUEST | 否 | 独立页面组件 | 采用 |
| R02 | `/login` | 登录 | GUEST | 否（设计模式可见） | `LoginCard` | 采用 |
| R03 | `/sso/bind` | SSO 绑定 | GUEST | 否 | 独立页面组件 | 适配 |
| R04 | `/` | 根重定向 | USER | 是 | 无（按角色重定向） | 采用 |
| R05 | `/quickstart` | 快速开始向导 | ADMIN | 否 | `QuickStartFlow` | 适配 |
| R06 | `/quickstart/minecraft` | Minecraft 预设 | ADMIN | 否 | `McPreset` | 适配 |
| R07 | `/instances` | 实例列表 | ADMIN | 是 | `InstanceList` | 采用 |
| R08 | `/instances/terminal` | 实例控制台 | USER | 否 | `Terminal` + `InstanceBaseInfo` + `InstanceManagerBtns` | 采用 |
| R09 | `/instances/terminal/files` | 文件管理 | USER | 否 | `InstanceFileManager` | 采用 |
| R10 | `/instances/terminal/mods` | 模组与插件 | USER | 否 | `InstanceModManager` | 适配 |
| R11 | `/instances/terminal/serverConfig` | 配置文件总览 | USER | 否 | `InstanceServerConfigOverview` | 采用 |
| R12 | `/instances/terminal/serverConfig/fileEdit` | 配置文件编辑 | USER | 否 | `InstanceServerConfigFile` | 采用 |
| R13 | `/instances/schedule` | 计划任务 | USER | 否 | `Schedule` | 采用 |
| R14 | `/market` | 应用市场 | ADMIN | 是 | `Market` | 排除 |
| R15 | `/market/editor` | 市场模板编辑器 | ADMIN | 否 | `MarketEditor` | 排除 |
| R16 | `/overview` | 面板总览 | ADMIN | 是 | 4×`StatusBlock` + `DataOverview` + `OperationLogCard` + `NodeOverview` + `RequestChart` + `InstanceChart` | 采用 |
| R17 | `/users` | 用户管理 | ADMIN | 是 | `UserList` | 采用 |
| R18 | `/users/resources` | 用户实例授权 | ADMIN | 否 | `UserAccessSettings` | 采用 |
| R19 | `/node` | 节点管理 | ADMIN | 是 | `NodeList` | 采用 |
| R20 | `/node/image` | 镜像管理 | ADMIN | 否 | `ImageManager` | 排除 |
| R21 | `/node/image/new` | 新建镜像 | ADMIN | 否 | `NewImage` | 排除 |
| R22 | `/settings` | 系统设置 | ADMIN | 是 | `Settings` | 采用 |
| R23 | `/user` | 个人设置 | ADMIN | 否 | 无默认卡片 | 适配 |
| R24 | `/customer` | 普通用户主页 | USER | 是 | 4×`UserStatusBlock` + `UserInstanceList` | 采用 |
| R25 | `/shop` | 商城 | GUEST | 是（条件） | `ShelvesCard` | 排除 |
| R26 | `/_open_page` | 自定义开放页 | ADMIN | 是 | `DefaultCard` | 适配 |
| R27 | `/404` | 未找到 | GUEST | 否 | `Page404` | 采用 |

**路由守卫行为** `[SOURCE]`：未初始化 → 强制跳 `/install`；`meta.redirect` 优先于一切；已登录访问 `/login` → 管理员跳 `/`，普通用户跳 `/customer`；权限不足且非管理员 → 跳 `/customer`；无匹配路由 → `/404`。

**导航形态** `[SOURCE]`：主菜单项由 `meta.mainMenu` 决定；面包屑由 `routersConfigOptimize()` 在启动时按路由树预计算并写入 `meta.breadcrumbs`。

## 4. 布局与卡片系统

`[SOURCE]` `panel/src/app/service/frontend_layout.ts`、`frontend/src/config/originLayoutConfig.ts`、`frontend/src/widgets/**`

| 编号 | 功能 | 说明 | 依赖厂商 | Servitium 分类 | 理由 | Servitium 需求 |
| --- | --- | --- | --- | --- | --- | --- |
| MCSM-001 | 页面布局持久化 | 布局存 `layout.json`，`GET /overview/layout` 读取，`POST` 保存，`DELETE` 重置 | 否 | 采用 | 仪表盘可定制是既定产品目标 | REQ-USR-040 |
| MCSM-002 | 卡片宽度（12 栅格） | `width: 1..12` | 否 | 采用 | — | REQ-USR-041 |
| MCSM-003 | 卡片高度档位 | MINI/SMALL/MEDIUM/BIG/LARGE/AUTO 六档固定像素 | 否 | 适配 | Servitium 改为语义档位并支持响应式 | REQ-USR-041 |
| MCSM-004 | 卡片拖拽排序 | 设计模式下拖拽 | 否 | 采用 | — | REQ-USR-042 |
| MCSM-005 | 卡片隐藏/删除 | `disableDelete` 标记核心卡片不可删除 | 否 | 采用 | — | REQ-USR-042 |
| MCSM-006 | 版本升级时补齐新页面布局 | 检测到版本变化时把新增页面追加进用户布局 | 否 | 采用 | 升级后不丢新功能入口 | REQ-USR-043 |
| MCSM-007 | 全局主题条目 `__settings__` | 页面标题、Logo、背景图、侧栏位置 | 否 | 适配 | Servitium 保留标题与侧栏位置，图片改为本地资源上传 | REQ-USR-044 |
| MCSM-008 | 文本卡片 | 自定义富文本 | 否 | 采用 | — | REQ-USR-045 |
| MCSM-009 | 图片卡片 / 图片框 | 展示图片 | 否 | 采用 | — | REQ-USR-045 |
| MCSM-010 | 链接卡片 | 跳转外链 | 否 | 采用 | — | REQ-USR-045 |
| MCSM-011 | 时钟卡片 | 显示时间 | 否 | 采用 | — | REQ-USR-045 |
| MCSM-012 | 内嵌 iframe 卡片 | 嵌入任意 URL | 否 | 适配 | Servitium 限制为受信任来源白名单，CSP 约束 | REQ-USR-046 |
| MCSM-013 | 音乐卡片 | 播放音频 | 否 | 排除 | 与服务端托管无关，增加媒体依赖 | — |
| MCSM-014 | 轮播卡片 | 图片轮播 | 否 | 排除 | 同上 | — |
| MCSM-015 | 插件卡片 | 预留扩展点 | 否 | 排除 | Servitium 不提供前端插件机制 | — |
| MCSM-016 | 空白占位卡片 | 布局留白 | 否 | 采用 | — | REQ-USR-045 |
| MCSM-017 | 卡片标题与描述 | 每张卡片可自定义标题与说明 | 否 | 采用 | — | REQ-USR-041 |

## 5. 身份、认证与账号

`[SOURCE]` `panel/src/app/routers/login_router.ts`、`general_user_router.ts`、`manage_user_router.ts`、`user_overview_router.ts`、`sso_router.ts`

| 编号 | 功能 | 后端行为 | 角色 | 依赖厂商 | 分类 | 理由 | 需求 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| MCSM-020 | 首次初始化 | `PUT /overview/install` 创建首个管理员 | GUEST | 否 | 适配 | Servitium 创建唯一 `Owner`，并强制密码强度与本机确认 | REQ-IDN-001 |
| MCSM-021 | 安装状态查询 | `ALL /auth/install` | GUEST | 否 | 采用 | — | REQ-IDN-002 |
| MCSM-022 | 用户名密码登录 | `POST /auth/login` | GUEST | 否 | 采用 | — | REQ-IDN-010 |
| MCSM-023 | 登出 | `GET /auth/logout` | USER | 否 | 采用 | — | REQ-IDN-011 |
| MCSM-024 | 会话令牌获取 | `GET /auth/token` | USER | 否 | 适配 | Servitium 用 CSRF 令牌 + HttpOnly Cookie 会话 | REQ-IDN-012 |
| MCSM-025 | 登录页信息 | `ALL /auth/login_info` 返回自定义版权文本与 SSO 状态 | GUEST | 否 | 采用 | — | REQ-IDN-013 |
| MCSM-026 | 登录状态 | `ALL /auth/status` | GUEST | 否 | 采用 | — | REQ-IDN-013 |
| MCSM-027 | TOTP 绑定 | `POST /auth/bind2fa` 生成密钥，`POST /auth/confirm2fa` 确认 | USER | 否 | 采用 | — | REQ-IDN-020 |
| MCSM-028 | TOTP 登录校验 | 登录时二次输入 6 位码 | GUEST | 否 | 采用 | — | REQ-IDN-021 |
| MCSM-029 | TOTP 时间漂移容忍 | `totpDriftToleranceSteps` 全局配置，单位 30 秒 | ADMIN | 否 | 采用 | — | REQ-IDN-022 |
| MCSM-030 | API Key 生成/更新 | `PUT /auth/api` 为当前用户生成 | USER | 否 | 适配 | Servitium 支持多把 Key、命名、过期与范围，且只在创建时明文展示一次 | REQ-IDN-025 |
| MCSM-031 | API Key 全局开关 | `enableApiKey` 系统设置，关闭后所有 Key 失效 | ADMIN | 否 | 采用 | — | REQ-IDN-026 |
| MCSM-032 | 修改本人资料/密码 | `PUT /auth/update` | USER | 否 | 采用 | — | REQ-IDN-027 |
| MCSM-033 | 用户名查询 | `GET /auth/query_username` | USER | 否 | 排除 | 无独立价值，Servitium 在用户列表内解决 | — |
| MCSM-034 | 创建用户 | `POST /auth/`（校验 `username`、`password`、`permission`） | ADMIN | 否 | 采用 | — | REQ-USR-001 |
| MCSM-035 | 搜索用户 | `GET /auth/search`，按用户名与角色过滤、分页 | ADMIN | 否 | 采用 | — | REQ-USR-002 |
| MCSM-036 | 修改用户 | `PUT /auth/` | ADMIN | 否 | 采用 | — | REQ-USR-003 |
| MCSM-037 | 删除用户 | `PUT /auth/`（批量 uuid） | ADMIN | 否 | 适配 | Servitium 分离「禁用」与「删除」，Owner 不可删 | REQ-USR-004 |
| MCSM-038 | 用户总览 | `GET /auth/overview` | ADMIN | 否 | 采用 | — | REQ-USR-005 |
| MCSM-039 | 登录 IP 限制 | `loginCheckIp` 开关，按 IP 计失败次数并临时封禁 | ADMIN | 否 | 采用 | — | REQ-IDN-040 |
| MCSM-040 | 临时封禁 IP 计数 | 总览页展示被封 IP 数与被拦截访问数 | ADMIN | 否 | 采用 | — | REQ-IDN-041 |

### 5.1 SSO / OIDC

`[SOURCE]` `panel/src/app/routers/sso_router.ts`、`panel/src/app/service/sso_service.ts`、`user_sso_service.ts`、`entity/setting.ts`

| 编号 | 功能 | 说明 | 依赖厂商 | 分类 | 理由 | 需求 |
| --- | --- | --- | --- | --- | --- | --- |
| MCSM-050 | SSO 开关与模式 | `ssoEnabled`、`ssoType ∈ {oidc, oauth2}` | 否 | 适配 | Servitium **仅支持 OIDC**，不实现通用 OAuth 2.0 模式 | REQ-IDN-050 |
| MCSM-051 | OIDC Issuer 发现 | 配置 `ssoIssuer`，自动发现端点 | 否 | 采用 | — | REQ-IDN-051 |
| MCSM-052 | OAuth2 手工端点 | `ssoAuthorizeUrl`/`ssoTokenUrl`/`ssoUserinfoUrl`/`ssoUserIdField`/`ssoScopes` | 否 | 排除 | 手工端点模式易配置错误且难以保证安全，Servitium 只保留标准 OIDC 发现 | — |
| MCSM-053 | Client ID / Secret | 共享配置 | 否 | 采用 | — | REQ-IDN-051 |
| MCSM-054 | 回调地址 | `ssoCallbackUrl` | 否 | 采用 | — | REQ-IDN-051 |
| MCSM-055 | 提供方显示名与图标 | `ssoProviderName`、`ssoIconUrl` | 否 | 适配 | 图标改为本地上传资源 | REQ-IDN-052 |
| MCSM-056 | 仅 SSO 模式 | `ssoOnlyMode` 隐藏本地登录 | 否 | 适配 | Servitium 保留 Owner 的本地登录作为逃生通道，不允许完全关闭 | REQ-IDN-053 |
| MCSM-057 | 自动跳转 | `ssoAutoRedirect` | 否 | 采用 | — | REQ-IDN-053 |
| MCSM-058 | 授权发起 | `GET /auth/sso/authorize` | 否 | 采用 | — | REQ-IDN-054 |
| MCSM-059 | 回调处理 | `GET /auth/sso/callback` | 否 | 采用 | Servitium 强制校验 `state`、`nonce` 与 subject 绑定 | REQ-IDN-055 |
| MCSM-060 | 绑定已有账号 | `POST /auth/sso/bind`、`POST /auth/sso/bind-current` | 否 | 采用 | — | REQ-IDN-056 |
| MCSM-061 | 解绑 | `PUT /auth/sso/unbind` | 否 | 采用 | — | REQ-IDN-057 |
| MCSM-062 | 配置变更告警 | 身份配置变更时弹危险确认 | 否 | 采用 | — | REQ-IDN-058 |
| MCSM-063 | SSO 绑定状态列 | 用户列表中展示是否已绑定 | 否 | 采用 | — | REQ-USR-002 |

## 6. 节点（Daemon）管理

`[SOURCE]` `panel/src/app/routers/daemon_router.ts`、`panel/src/app/entity/remote_service.ts`、`daemon/src/entity/config.ts`、`frontend/src/widgets/node/**`

| 编号 | 功能 | 说明 | 依赖厂商 | 分类 | 理由 | 需求 |
| --- | --- | --- | --- | --- | --- | --- |
| MCSM-070 | 节点列表 | `GET /service/remote_services_list` 精简列表；`GET /service/remote_services` 详细列表 | 否 | 采用 | — | REQ-NOD-001 |
| MCSM-071 | 节点系统信息 | `GET /service/remote_services_system` 返回 CPU/内存/负载 | 否 | 采用 | — | REQ-NOD-002 |
| MCSM-072 | 新增节点 | `POST /service/remote_service`，填 IP、端口、密钥、路径前缀、备注 | 否 | **适配** | Servitium 反转连接方向：Agent 主动连控制平面，改为一次性注册令牌 | REQ-NOD-010 |
| MCSM-073 | 修改节点 | `PUT /service/remote_service` | 否 | 适配 | — | REQ-NOD-011 |
| MCSM-074 | 删除节点 | `DELETE /service/remote_service` | 否 | 采用 | — | REQ-NOD-012 |
| MCSM-075 | 重连节点 | `GET /service/link_remote_service` | 否 | 适配 | Servitium 由 Agent 侧自动重连，控制平面提供「强制重连」指令 | REQ-NOD-013 |
| MCSM-076 | 节点密钥 | 守护进程自生成 64 位随机密钥，面板保存明文用于连接 | 否 | **适配** | Servitium 改为控制平面签发的可轮换节点凭据，只存哈希 | REQ-NOD-020 |
| MCSM-077 | 路径前缀 | 反向代理下的子路径 | 否 | 采用 | — | REQ-NOD-011 |
| MCSM-078 | 远程直连地址映射 | `remoteMappings`：把面板侧地址映射为浏览器可直连的地址，用于终端与文件流 | 否 | **适配** | Servitium 的浏览器只连接控制平面；节点地址映射改为玩家连接地址与诊断信息 | REQ-NOD-030 |
| MCSM-079 | 节点监听端口 | `daemonPort` | 否 | 不适用 | Servitium 中 Agent 不监听入站端口 | — |
| MCSM-080 | 上传限速 | `uploadSpeedRate`，档位 0/320KB/s…30MB/s | 否 | 采用 | — | REQ-NOD-031 |
| MCSM-081 | 下载限速 | `downloadSpeedRate`，同档位 | 否 | 采用 | — | REQ-NOD-031 |
| MCSM-082 | URL 下载并发上限 | `maxDownloadFromUrlFileCount` | 否 | 采用 | — | REQ-FIL-060 |
| MCSM-083 | 终端输出缓冲区 | `outputBufferSize`，块数，16–4096，每块 256 字符，50 ms 刷新 | 否 | 适配 | Servitium 以字节数与行数双限，见终端规格 | REQ-TRM-030 |
| MCSM-084 | 优雅关机开关 | `enableSoftShutdown` | 否 | 采用 | Windows 关机时优雅停止实例 | REQ-INS-070 |
| MCSM-085 | 优雅关机跳过容器 | `softShutdownSkipDocker` | 否 | 不适用 | 无 Docker | — |
| MCSM-086 | 优雅关机等待秒数 | `softShutdownWaitSeconds`，默认 30 | 否 | 采用 | — | REQ-INS-071 |
| MCSM-087 | 面板 IP 白名单 | `whiteListPanelIp`、`whiteListPanelIps` | 否 | 不适用 | Agent 不接受入站连接 | — |
| MCSM-088 | 守护进程 TLS | `ssl`、`sslPemPath`、`sslKeyPath` | 否 | 适配 | Servitium 的 Agent→Server 连接固定 TLS + 双向凭据 | REQ-NOD-021 |
| MCSM-089 | 默认实例路径 | `defaultInstancePath` | 否 | 采用 | — | REQ-INS-020 |
| MCSM-090 | 默认 Java 数据路径 | `defaultJavaDataPath` | 否 | 采用 | — | REQ-JRE-020 |
| MCSM-091 | 端口自动分配 | `allocatablePortRange`、`currentAllocatablePort`、`portAssignInterval` | 否 | 适配 | Servitium 提供端口占用检测与建议，但不自动改写实例配置 | REQ-INS-021 |
| MCSM-092 | 最大文件任务数 | `maxFileTask` | 否 | 采用 | — | REQ-FIL-061 |
| MCSM-093 | 最大压缩文件大小 | `maxZipFileSize`（MB） | 否 | 采用 | — | REQ-FIL-062 |
| MCSM-094 | 节点版本一致性提示 | 前端比对面板期望的守护进程版本，不一致时以危险色提示 | 否 | 采用 | Servitium 用协议版本协商 | REQ-NOD-040 |
| MCSM-095 | 节点在线状态 | `available` 布尔 + 前端连接可达性分别展示 | 否 | 采用 | — | REQ-NOD-041 |
| MCSM-096 | 节点实例统计 | 运行中/总数 | 否 | 采用 | — | REQ-NOD-042 |
| MCSM-097 | 节点平台信息 | 操作系统与架构 | 否 | 采用 | — | REQ-NOD-042 |
| MCSM-098 | 节点 ID 复制 | 一键复制 Daemon ID | 否 | 采用 | — | REQ-NOD-042 |
| MCSM-099 | 节点级文件管理入口 | 从节点卡片直接进入该节点的文件管理 | 否 | 排除 | Servitium 的文件管理严格绑定实例工作目录，不提供节点级全盘浏览 | — |
| MCSM-100 | 节点级终端入口 | 从节点卡片打开节点全局终端 | 否 | **排除** | 全局 WebShell 是明确排除项 | — |

## 7. 实例管理

`[SOURCE]` `panel/src/app/routers/instance_admin_router.ts`、`instance_operate_router.ts`、`daemon/src/routers/Instance_router.ts`、`daemon/src/entity/instance/**`

### 7.1 实例状态与类型

`[SOURCE]` `daemon/src/entity/instance/instance.ts`

| MCSManager 状态 | 值 | Servitium 对应 |
| --- | --- | --- |
| `STATUS_BUSY` | -1 | `Busy`（长操作占用） |
| `STATUS_STOP` | 0 | `Stopped` |
| `STATUS_STOPPING` | 1 | `Stopping` |
| `STATUS_STARTING` | 2 | `Starting` |
| `STATUS_RUNNING` | 3 | `Running` |
| —（无） | — | `Crashed`（Servitium 新增，用于区分异常退出） |
| —（无） | — | `Unreachable`（Servitium 新增，节点离线时的实例状态） |

类型：`universal`、`minecraft/java`、`minecraft/bedrock`。Servitium **仅保留 Minecraft Java**。

### 7.2 实例功能

| 编号 | 功能 | 说明 | 分类 | 理由 | 需求 |
| --- | --- | --- | --- | --- | --- |
| MCSM-110 | 实例分页列表 | `GET /instance/`，按节点、名称、状态、标签过滤，分页 | 采用 | — | REQ-INS-001 |
| MCSM-111 | 全节点聚合列表 | `GET /service/remote_services_instances_global`，树形展示「节点 → 实例」，每节点独立分页与「加载更多」 | 采用 | 多节点管理的核心视图 | REQ-INS-002 |
| MCSM-112 | 实例详情 | `GET /service/remote_service_instances` | 采用 | — | REQ-INS-003 |
| MCSM-113 | 新建实例 | `POST /instance/` | 采用 | — | REQ-INS-010 |
| MCSM-114 | 上传压缩包创建实例 | `POST /instance/upload` | 采用 | — | REQ-INS-011 |
| MCSM-115 | 附加已有目录创建实例 | 创建向导中的「已存在」方式，直接填写 `cwd` | 采用 | 与「工作目录是实例属性」的决策一致 | REQ-INS-012 |
| MCSM-116 | 修改实例配置 | `PUT /instance/` | 采用 | — | REQ-INS-013 |
| MCSM-117 | 删除实例 | `DELETE /instance/`，`deleteFile` 布尔决定是否删目录 | **适配** | Servitium 严格分离「删除登记」与「删除目录」，且外部工作目录永不递归删除 | REQ-INS-014、REQ-INS-015 |
| MCSM-118 | 批量启动 | `POST /instance/multi_open` | 采用 | — | REQ-INS-030 |
| MCSM-119 | 批量停止 | `POST /instance/multi_stop` | 采用 | — | REQ-INS-030 |
| MCSM-120 | 批量强制终止 | `POST /instance/multi_kill` | 采用 | — | REQ-INS-030 |
| MCSM-121 | 批量重启 | `POST /instance/multi_restart` | 采用 | — | REQ-INS-030 |
| MCSM-122 | 批量删除 | 批量 uuid + `deleteFile` | 适配 | 同 MCSM-117 | REQ-INS-030 |
| MCSM-123 | 启动 | `ALL /protected_instance/open` | 采用 | — | REQ-INS-040 |
| MCSM-124 | 停止 | `ALL /protected_instance/stop` | 采用 | — | REQ-INS-041 |
| MCSM-125 | 重启 | `ALL /protected_instance/restart` | 采用 | — | REQ-INS-042 |
| MCSM-126 | 强制终止 | `ALL /protected_instance/kill` | 采用 | — | REQ-INS-043 |
| MCSM-127 | 发送控制台命令 | `ALL /protected_instance/command` | 采用 | — | REQ-TRM-010 |
| MCSM-128 | 异步任务（更新/安装） | `POST /protected_instance/asynchronous`，`stop_asynchronous`，`query_asynchronous` | 适配 | Servitium 统一为长操作模型 `OperationId` | REQ-INS-050 |
| MCSM-129 | 更新/安装命令 | `updateCommand` 字段，独立于启动命令 | 适配 | Servitium 改为受限的「更新动作」配置 | REQ-INS-051 |
| MCSM-130 | 实例更新 | `PUT /protected_instance/instance_update` | 采用 | — | REQ-INS-051 |
| MCSM-131 | 输出日志读取 | `GET /protected_instance/outputlog` | 采用 | — | REQ-TRM-040 |
| MCSM-132 | 快速安装列表 | `GET /instance/quick_install_list` 从厂商 URL 拉取预设包 | **是（厂商）** | 排除 | 明确排除项 | — |
| MCSM-133 | 安装实例（从预设） | `POST /protected_instance/install_instance` | 是（厂商） | 排除 | 同上 | — |
| MCSM-134 | 任意请求转发 | `ALL /instance/forward` 把任意载荷转发到守护进程 | 否 | **排除** | 通用转发通道是横向越权风险源，Servitium 使用显式定义的 gRPC 方法 | — |
| MCSM-135 | 实例标签 | `tag: string[]`，列表页可按标签筛选，标签云 | 采用 | — | REQ-INS-004 |
| MCSM-136 | 实例到期时间 | `endTime`，到期后自动停止 | 排除 | 属于商业售卖能力 | — |
| MCSM-137 | Minecraft Server List Ping | `pingConfig{ip, port, type}`，展示在线/最大玩家数 | 采用 | — | REQ-INS-060 |
| MCSM-138 | RCON | `enableRcon`、`rconIp`、`rconPort`、`rconPassword` | 采用 | — | REQ-INS-061 |
| MCSM-139 | 自定义快捷命令列表 | `actionCommandList[{name, command}]` | 采用 | — | REQ-TRM-011 |
| MCSM-140 | 自动启动 | `eventTask.autoStart` | 采用 | — | REQ-INS-062 |
| MCSM-141 | 自动重启 | `eventTask.autoRestart` | 采用 | — | REQ-INS-063 |
| MCSM-142 | 自动重启次数上限 | `eventTask.autoRestartMaxTimes`，-1 为无限 | 采用 | — | REQ-INS-063 |
| MCSM-143 | 实例观察者计数 | `watcher`，显示同时观看该终端的人数 | 采用 | — | REQ-TRM-021 |
| MCSM-144 | EULA 校验 | 启动前检测 `eula.txt` 并提示同意 | 采用 | Minecraft Java 必需 | REQ-INS-064 |
| MCSM-145 | 进程类型 | `processType ∈ {general, docker}` | 不适用 | 无 Docker | — |
| MCSM-146 | 以指定用户运行 | `runAs` | 适配 | Servitium 支持指定 Windows 账户运行，需显式凭据管理 | REQ-INS-065 |
| MCSM-147 | 基础端口 | `basePort` | 采用 | 用于端口冲突检测 | REQ-INS-021 |
| MCSM-148 | 停止命令与超时 | `stopCommand`（支持 `^C`）、`stopTimeout` | 采用 | — | REQ-INS-041 |
| MCSM-149 | 实例导入/导出交换 | `POST /exchange/`、`GET /exchange/sso`、`POST /exchange/request_buy_instance` | 是（厂商） | 排除 | 商业兑换能力 | — |
| MCSM-150 | 模板保存/应用 | 把实例配置存为市场模板 | 是（厂商） | 排除 | 模板市场是明确排除项 | — |

### 7.3 Java 运行时

`[SOURCE]` `panel/src/app/routers/java_manager_router.ts`、`daemon/src/service/java_manager.ts`

| 编号 | 功能 | 说明 | 依赖厂商 | 分类 | 理由 | 需求 |
| --- | --- | --- | --- | --- | --- | --- |
| MCSM-160 | Java 列表 | `GET /java_manager/list` | 否 | 采用 | — | REQ-JRE-001 |
| MCSM-161 | 手工添加 Java | `POST /java_manager/add` | 否 | 采用 | — | REQ-JRE-010 |
| MCSM-162 | 在线下载 Java | `POST /java_manager/download`，调用 Azul Zulu 元数据 API | **是** | 排除 | 厂商在线目录，明确排除；Servitium 改为导入本地压缩包 | REQ-JRE-011（替代） |
| MCSM-163 | 为实例选用 Java | `POST /java_manager/using` | 否 | 采用 | — | REQ-JRE-030 |
| MCSM-164 | 删除 Java | `DELETE /java_manager/delete` | 否 | 适配 | Servitium 区分「取消登记」与「删除导入文件」 | REQ-JRE-040 |
| MCSM-165 | Java 使用中实例列表 | `usingInstances` | 否 | 采用 | 删除前必须提示占用 | REQ-JRE-041 |
| MCSM-166 | Java 安装时间 | `installTime` | 否 | 采用 | — | REQ-JRE-001 |
| MCSM-167 | Java 版本解析 | 从 `JavaInfo` 读取 name/version | 否 | 适配 | Servitium 额外解析架构与厂商并做可执行性验证 | REQ-JRE-012 |
| MCSM-168 | 自动探测已装 Java | 无（MCSManager 不做系统探测） | 否 | 不适用 | Servitium **新增**：注册表、`JAVA_HOME`、`PATH`、常见目录探测 | REQ-JRE-002 |

## 8. 终端与进程

`[SOURCE]` `daemon/src/routers/stream_router.ts`、`panel/src/app/routers/instance_operate_router.ts :: /stream_channel`、`frontend/src/widgets/instance/Terminal.vue`、`common/src/instance_stream.ts`

| 编号 | 功能 | 说明 | 分类 | 理由 | 需求 |
| --- | --- | --- | --- | --- | --- |
| MCSM-180 | 终端通道申请 | `POST /protected_instance/stream_channel` 由面板向守护进程注册一次性口令，返回 `{password, addr, prefix, remoteMappings}` | **适配** | Servitium 改为控制平面内的 SignalR Hub，浏览器不直连节点 | REQ-TRM-001 |
| MCSM-181 | 终端鉴权 | 守护进程 `stream/auth` 校验一次性口令 | 适配 | 同上 | REQ-TRM-001 |
| MCSM-182 | 终端输出转发 | `InstanceSubsystem.forward` 把实例输出推给该 socket | 采用 | — | REQ-TRM-020 |
| MCSM-183 | 终端输入 | `stream/input`、`stream/write` | 采用 | — | REQ-TRM-010 |
| MCSM-184 | PTY 尺寸调整 | `stream/resize` | 采用 | — | REQ-TRM-012 |
| MCSM-185 | 终端详情 | `stream/detail` | 采用 | — | REQ-TRM-021 |
| MCSM-186 | 伪终端开关 | `terminalOption.pty` | 采用 | Servitium 以 ConPTY 实现 | REQ-TRM-002 |
| MCSM-187 | 颜色渲染开关 | `terminalOption.haveColor` | 采用 | — | REQ-TRM-022 |
| MCSM-188 | PTY 初始列/行 | `ptyWindowCol=164`、`ptyWindowRow=40` | 采用 | — | REQ-TRM-002 |
| MCSM-189 | 输入编码 | `ie` | 采用 | — | REQ-TRM-023 |
| MCSM-190 | 输出编码 | `oe` | 采用 | — | REQ-TRM-023 |
| MCSM-191 | 换行模式 | `crlf ∈ {1:LF, 2:CRLF}`，Windows 默认 CRLF | 采用 | — | REQ-TRM-024 |
| MCSM-192 | 输出历史缓冲 | 环形缓冲，按块数配置 | 采用 | — | REQ-TRM-030 |
| MCSM-193 | 多客户端同时观察 | 同一实例可被多个会话观察 | 采用 | — | REQ-TRM-021 |
| MCSM-194 | 断线重连 | 前端自动重连 socket | 采用 | — | REQ-TRM-031 |
| MCSM-195 | 内嵌/独立终端两种形态 | `viewType: inner` 决定在页面内嵌还是独占 | 适配 | Servitium 保留内嵌形态，独立形态改为浏览器新标签/Desktop 独立窗口 | REQ-TRM-032 |
| MCSM-196 | 会话日志导出 | 无（MCSManager 仅可读实时输出与 `outputlog`） | 不适用 | Servitium **新增**：导出当前会话缓冲为文本文件 | REQ-TRM-033 |
| MCSM-197 | 进程树控制 | 无（依赖 `process_tools` 逐级 kill） | 适配 | Servitium **强化**：Windows Job Object 保证完整进程树 | REQ-TRM-003 |

## 9. 文件管理

`[SOURCE]` `panel/src/app/routers/filemananger_router.ts`、`daemon/src/routers/file_router.ts`、`daemon/src/service/system_file.ts`、`frontend/src/widgets/instance/FileManager.vue`

| 编号 | 功能 | 说明 | 分类 | 理由 | 需求 |
| --- | --- | --- | --- | --- | --- |
| MCSM-210 | 目录列表 | `GET /files/list`，分页、按名称搜索、排序 | 采用 | — | REQ-FIL-001 |
| MCSM-211 | 文件系统状态 | `GET /files/status`，返回磁盘列表与进行中任务数 | 采用 | — | REQ-FIL-002 |
| MCSM-212 | Windows 盘符列表 | 枚举 A–Z 存在的盘符 | 适配 | Servitium 不提供盘符切换，只显示实例工作目录所在卷的容量 | REQ-FIL-003 |
| MCSM-213 | 新建文件 | `POST /files/touch` | 采用 | — | REQ-FIL-010 |
| MCSM-214 | 新建目录 | `POST /files/mkdir` | 采用 | — | REQ-FIL-010 |
| MCSM-215 | 重命名 | `PUT /files/` | 采用 | — | REQ-FIL-011 |
| MCSM-216 | 复制 | `POST /files/copy` | 采用 | — | REQ-FIL-012 |
| MCSM-217 | 移动/剪切 | `PUT /files/move` | 采用 | — | REQ-FIL-013 |
| MCSM-218 | 删除 | `DELETE /files/` | 采用 | — | REQ-FIL-014 |
| MCSM-219 | 批量选择与批量操作 | 多选后批量删除/复制/剪切/压缩 | 采用 | — | REQ-FIL-015 |
| MCSM-220 | 剪贴板 | 跨目录粘贴 | 采用 | — | REQ-FIL-013 |
| MCSM-221 | 上传文件 | `ALL /files/upload`，带限速与并发限制 | 适配 | Servitium 改为分片上传 + 断点续传 | REQ-FIL-020 |
| MCSM-222 | 上传文件夹 | 前端目录选择 | 采用 | — | REQ-FIL-021 |
| MCSM-223 | 上传进度与暂停 | 前端进度条、暂停/继续 | 采用 | — | REQ-FIL-022 |
| MCSM-224 | 下载 | `ALL /files/download` | 适配 | Servitium 支持 `Range` 与流式下载 | REQ-FIL-030 |
| MCSM-225 | 从 URL 下载 | `POST /files/download_from_url`，带并发上限 | 采用 | Servitium 增加 SSRF 防护 | REQ-FIL-031 |
| MCSM-226 | 取消 URL 下载 | `POST /files/download_from_url_stop` | 采用 | — | REQ-FIL-032 |
| MCSM-227 | URL 下载任务进度 | 前端展示进度条与状态 | 采用 | — | REQ-FIL-033 |
| MCSM-228 | 压缩 | `POST /files/compress`（压缩/解压共用），可指定归档名 | 采用 | — | REQ-FIL-040 |
| MCSM-229 | 解压 | 同上，模式 0=当前目录，1=新建目录 | 采用 | — | REQ-FIL-041 |
| MCSM-230 | 解压编码选择 | UTF-8 / GBK / BIG5 | 采用 | 中文归档必需 | REQ-FIL-042 |
| MCSM-231 | 压缩实现切换 | `zipType` 系统设置，内建或 7-Zip | 适配 | Servitium 统一使用内建实现，不外挂 7-Zip | REQ-FIL-043 |
| MCSM-232 | 最大压缩任务数 | `maxCompress` | 采用 | — | REQ-FIL-062 |
| MCSM-233 | 文本编辑 | `file/edit`，读取与保存 | 采用 | — | REQ-FIL-050 |
| MCSM-234 | 文件编码 | 实例级 `fileCode` | 采用 | — | REQ-FIL-051 |
| MCSM-235 | 编辑器 | Monaco，含快捷键保存 | 采用 | — | REQ-FIL-052 |
| MCSM-236 | 面包屑与路径直接编辑 | 点击面包屑切换目录；可切换为输入框直接键入路径 | 采用 | — | REQ-FIL-004 |
| MCSM-237 | 多标签页 | 文件管理支持多个目录标签 | 采用 | — | REQ-FIL-005 |
| MCSM-238 | 权限修改 | `PUT /files/chmod`、`PUT /files/chmod_batch`，rwx 复选 + 递归 | **不适用** | Windows 无 POSIX 权限位 | — |
| MCSM-239 | 权限列 | 文件表中的 `mode` 列 | 不适用 | 同上 | — |
| MCSM-240 | 普通用户文件管理开关 | `canFileManager` 系统设置 | 采用 | — | REQ-FIL-063 |
| MCSM-241 | 路径穿越防护 | `checkPath` 基于归一化后的前缀比较 | 适配 | Servitium 强化：UNC、设备路径、ADS、重解析点全部拦截 | REQ-FIL-070 |
| MCSM-242 | 符号链接真实路径检查 | `resolveRealPath` | 采用 | — | REQ-FIL-070 |
| MCSM-243 | 文件名合法性校验 | `checkFileName` | 采用 | Servitium 增加 Windows 保留名检查 | REQ-FIL-071 |
| MCSM-244 | 传输限速 | 面板与节点两级 `speedLimit` 中间件 | 采用 | — | REQ-FIL-060 |
| MCSM-245 | 图片预览 | `ImageViewer` 对话框 | 采用 | — | REQ-FIL-053 |

## 10. 配置管理

`[SOURCE]` `panel/src/app/routers/instance_operate_router.ts :: /process_config/*`、`daemon/src/entity/instance/process_config.ts`、`frontend/src/config/instanceConfigMap.ts`、`frontend/src/widgets/instance/ServerConfigOverview.vue`、`ServerConfigFile.vue`

| 编号 | 功能 | 说明 | 分类 | 理由 | 需求 |
| --- | --- | --- | --- | --- | --- |
| MCSM-260 | 配置文件识别 | `POST /protected_instance/process_config/list`，按已知文件名清单在工作目录中匹配 | 采用 | — | REQ-CFG-001 |
| MCSM-261 | 读取配置 | `GET /protected_instance/process_config/file` | 采用 | — | REQ-CFG-002 |
| MCSM-262 | 写入配置 | `PUT /protected_instance/process_config/file` | 采用 | — | REQ-CFG-003 |
| MCSM-263 | 结构化表单渲染 | 按 `instanceConfigMap` 中的字段定义渲染表单控件 | 采用 | — | REQ-CFG-010 |
| MCSM-264 | `server.properties` 支持 | 内置字段定义 | 采用 | — | REQ-CFG-011 |
| MCSM-265 | 多格式支持 | properties / JSON / YAML / TOML / 纯文本 | 采用 | — | REQ-CFG-012 |
| MCSM-266 | 冲突提示 | 同一逻辑配置存在多个候选文件时标记冲突 | 采用 | — | REQ-CFG-013 |
| MCSM-267 | 切换到原始文本编辑 | 从结构化表单跳到文件编辑器 | 采用 | — | REQ-CFG-014 |
| MCSM-268 | 保存前无损往返 | MCSManager 按格式解析后整体重写，注释与顺序**会丢失** | **适配** | Servitium 强制无损往返（注释、顺序、数值精度） | REQ-CFG-020 |
| MCSM-269 | 保存前备份 | 无 | 不适用 | Servitium **新增**：写入前生成带时间戳的备份 | REQ-CFG-021 |
| MCSM-270 | 语法校验 | 无 | 不适用 | Servitium **新增**：保存前按格式校验 | REQ-CFG-022 |
| MCSM-271 | 原子保存 | 无（直接覆盖） | 不适用 | Servitium **新增**：临时文件 + 原子替换 | REQ-CFG-023 |

## 11. 模组与插件

`[SOURCE]` `panel/src/app/routers/mod_manager_router.ts`、`daemon/src/service/mod_service.ts`、`frontend/src/widgets/instance/ModManager.vue`

| 编号 | 功能 | 说明 | 依赖厂商 | 分类 | 理由 | 需求 |
| --- | --- | --- | --- | --- | --- | --- |
| MCSM-280 | 本地模组列表 | `GET /mod/list`，扫描 `mods/` | 否 | 采用 | — | REQ-MOD-001 |
| MCSM-281 | 本地插件列表 | 同上，扫描 `plugins/` | 否 | 采用 | — | REQ-MOD-001 |
| MCSM-282 | 目录存在性判定 | 无 `mods/` 时不显示该标签页 | 否 | 采用 | — | REQ-MOD-002 |
| MCSM-283 | 模组元数据解析 | `GET /mod/info`，读取 JAR 内元数据 | 否 | 采用 | Servitium 解析 `fabric.mod.json`、`quilt.mod.json`、`META-INF/mods.toml`、`META-INF/neoforge.mods.toml`、`mcmod.info`、`plugin.yml`、`paper-plugin.yml` | REQ-MOD-003 |
| MCSM-284 | 批量元数据 | `POST /mod/batch_info` | 否 | 采用 | — | REQ-MOD-003 |
| MCSM-285 | 启用/停用 | `POST /mod/toggle`，通过 `.disabled` 后缀 | 否 | 采用 | — | REQ-MOD-010 |
| MCSM-286 | 删除 | `POST /mod/delete` | 否 | 采用 | — | REQ-MOD-011 |
| MCSM-287 | 上传模组 | 通过文件管理上传 | 否 | 适配 | Servitium 在模组页直接提供上传入口 | REQ-MOD-012 |
| MCSM-288 | 模组配置文件入口 | `GET /mod/config_files` 列出该模组关联的配置文件 | 否 | 采用 | — | REQ-MOD-013 |
| MCSM-289 | 模组数量徽标 | 标签页上显示数量 | 否 | 采用 | — | REQ-MOD-002 |
| MCSM-290 | 搜索本地模组 | 前端按名称过滤 | 否 | 采用 | — | REQ-MOD-004 |
| MCSM-291 | Windows 文件锁告警 | 实例运行中在 Windows 上操作模组文件时弹出警告 | 否 | **采用** | Windows 平台必需 | REQ-MOD-014 |
| MCSM-292 | 在线搜索模组 | `GET /mod/search`，源为 Modrinth / CurseForge / SpigotMC | **是** | **排除** | 第三方在线市场，明确排除项 | — |
| MCSM-293 | 在线版本列表 | `GET /mod/versions` | 是 | 排除 | 同上 | — |
| MCSM-294 | 在线下载模组 | `POST /mod/download` | 是 | 排除 | 同上 | — |
| MCSM-295 | 停止在线传输 | `POST /mod/stop_transfer` | 是 | 排除 | 同上 | — |
| MCSM-296 | Minecraft 版本列表 | `GET /mod/mc_versions` 用于在线搜索过滤 | 是 | 排除 | 同上 | — |
| MCSM-297 | 加载器/平台筛选 | 在线搜索的筛选条件 | 是 | 排除 | 同上 | — |

## 12. 计划任务

`[SOURCE]` `panel/src/app/routers/schedule_router.ts`、`daemon/src/service/system_instance_control.ts`

| 编号 | 功能 | 说明 | 分类 | 理由 | 需求 |
| --- | --- | --- | --- | --- | --- |
| MCSM-310 | 任务列表 | `GET /protected_schedule/` | 采用 | — | REQ-SCH-001 |
| MCSM-311 | 创建任务 | `POST /protected_schedule/` | 采用 | — | REQ-SCH-010 |
| MCSM-312 | 删除任务 | `DELETE /protected_schedule/` | 采用 | — | REQ-SCH-011 |
| MCSM-313 | 间隔触发 | `type=1`，秒为单位，最小 3 秒 | 采用 | — | REQ-SCH-020 |
| MCSM-314 | 周期触发 | `type=2`，指定时刻 + 星期集合，底层为 cron 表达式 | 采用 | — | REQ-SCH-021 |
| MCSM-315 | 指定时刻触发 | `type=3`，一次性 | 采用 | — | REQ-SCH-022 |
| MCSM-316 | 剩余执行次数 | `count`，-1 为无限，减到 0 自动注销 | 采用 | — | REQ-SCH-023 |
| MCSM-317 | 有序动作链 | `actions[{type, payload}]`，上限 10 个 | 采用 | — | REQ-SCH-030 |
| MCSM-318 | 动作类型 | `delay`、`command`、`start`、`stop`、`restart`、`kill` | 采用 | — | REQ-SCH-031 |
| MCSM-319 | 每实例任务数上限 | 8 | 适配 | Servitium 提高到 32 并可配置 | REQ-SCH-040 |
| MCSM-320 | 任务名唯一性与合法性 | 同实例内不重名，文件名合法 | 采用 | — | REQ-SCH-041 |
| MCSM-321 | 节点本地存储与执行 | 任务持久化在节点，控制平面离线仍执行 | **采用** | 与「控制平面离线时本地自治」的决策一致 | REQ-SCH-050 |
| MCSM-322 | 启用/停用 | 无（只能删除重建） | 不适用 | Servitium **新增** | REQ-SCH-012 |
| MCSM-323 | 立即执行一次 | 无 | 不适用 | Servitium **新增** | REQ-SCH-013 |
| MCSM-324 | 执行历史 | 无 | 不适用 | Servitium **新增** | REQ-SCH-042 |
| MCSM-325 | 重叠触发策略 | 无（可能并发执行） | 不适用 | Servitium **新增**：跳过 / 排队 / 并发 三选一 | REQ-SCH-043 |

## 13. 总览、日志与审计

`[SOURCE]` `panel/src/app/routers/overview_router.ts`、`panel/src/app/service/operation_logger.ts`、`visual_data.ts`、`user_statistics.ts`

| 编号 | 功能 | 说明 | 分类 | 理由 | 需求 |
| --- | --- | --- | --- | --- | --- |
| MCSM-340 | 面板总览数据 | `GET /overview/` 聚合节点、实例、用户、系统信息 | 采用 | — | REQ-USR-020 |
| MCSM-341 | 节点在线/总数 | `StatusBlock type=node` | 采用 | — | REQ-USR-021 |
| MCSM-342 | 实例运行/总数 | `StatusBlock type=instance` | 采用 | — | REQ-USR-021 |
| MCSM-343 | 登录成功/失败数 | `StatusBlock type=users` | 采用 | — | REQ-USR-021 |
| MCSM-344 | 系统 CPU/内存 | `StatusBlock type=system` | 采用 | — | REQ-USR-021 |
| MCSM-345 | 请求量图表 | `RequestChart` | 采用 | — | REQ-USR-022 |
| MCSM-346 | 实例数量图表 | `InstanceChart` | 采用 | — | REQ-USR-022 |
| MCSM-347 | 节点概览表 | `NodeOverview`，列：地址、备注、CPU、内存、实例数、版本、状态 | 采用 | — | REQ-USR-023 |
| MCSM-348 | 系统诊断信息 | `PanelOverview`：运行时版本、面板版本、期望节点版本、运行账户、面板时间、浏览器时间、被封 IP 数、被拦截访问数、内存、CPU、负载、进程内存、主机名、系统版本与类型 | 采用 | — | REQ-USR-024 |
| MCSM-349 | 操作日志 | `GET /overview/operation_logs`，JSONL 追加存储，带缓冲与定时刷盘 | 采用 | — | REQ-USR-030 |
| MCSM-350 | 日志级别 | info / warning / error | 采用 | — | REQ-USR-030 |
| MCSM-351 | 日志时间线卡片 | `OperationLogCard` | 采用 | — | REQ-USR-031 |
| MCSM-352 | 安全审计 | 无独立审计流（与操作日志混合） | 适配 | Servitium **分离**操作日志与安全审计日志，审计日志不可修改 | REQ-USR-032 |
| MCSM-353 | 用户视角总览 | `/customer` 页面的 4 个统计块 + 授权实例列表 | 采用 | — | REQ-USR-025 |

## 14. 系统设置

`[SOURCE]` `panel/src/app/routers/settings_router.ts`、`panel/src/app/entity/setting.ts`、`frontend/src/widgets/Settings.vue`

设置页分组：基础、外观、安全、第三方登录、赞助、关于（另有被注释掉的「在线功能」「卡密销售」两组）。

| 编号 | 设置项 | 说明 | 依赖厂商 | 分类 | 需求 |
| --- | --- | --- | --- | --- | --- |
| MCSM-360 | 面板语言 | `language` | 否 | 采用 | REQ-USR-050 |
| MCSM-361 | 面板端口 | `httpPort`（默认 23333） | 否 | 采用 | REQ-USR-051 |
| MCSM-362 | 面板绑定 IP | `httpIp` | 否 | **适配** | Servitium 默认仅回环，非回环需显式确认与 TLS | REQ-USR-052 |
| MCSM-363 | 路径前缀 | `prefix` | 否 | 采用 | REQ-USR-053 |
| MCSM-364 | 数据传输端口 | `dataPort` | 否 | 不适用 | Servitium 单端口 | — |
| MCSM-365 | 分布式转发模式 | `forwardType` | 否 | 不适用 | Servitium 固定由控制平面中转 | — |
| MCSM-366 | 跨域开关 | `crossDomain` | 否 | 适配 | Servitium 改为显式来源白名单 | REQ-USR-054 |
| MCSM-367 | Gzip 压缩 | `gzip` | 否 | 采用 | REQ-USR-055 |
| MCSM-368 | 反向代理模式 | `reverseProxyMode`、`reverseProxyHeader`（默认 `X-Real-IP`） | 否 | 采用 | REQ-USR-056 |
| MCSM-369 | 最大并发压缩任务 | `maxCompress` | 否 | 采用 | REQ-FIL-062 |
| MCSM-370 | 最大并发下载任务 | `maxDownload` | 否 | 采用 | REQ-FIL-060 |
| MCSM-371 | 解压实现选择 | `zipType` | 否 | 适配 | 见 MCSM-231 | REQ-FIL-043 |
| MCSM-372 | TOTP 漂移容忍 | `totpDriftToleranceSteps` | 否 | 采用 | REQ-IDN-022 |
| MCSM-373 | 登录 IP 校验 | `loginCheckIp` | 否 | 采用 | REQ-IDN-040 |
| MCSM-374 | 登录页自定义文本 | `loginInfo` | 否 | 采用 | REQ-USR-057 |
| MCSM-375 | 普通用户文件管理开关 | `canFileManager` | 否 | 采用 | REQ-FIL-063 |
| MCSM-376 | 允许模板重置实例 | `allowUsePreset` | 是 | 排除 | 依赖模板市场 | — |
| MCSM-377 | 允许改容器命令 | `allowChangeCmd` | 否 | 不适用 | 无 Docker | — |
| MCSM-378 | API Key 总开关 | `enableApiKey` | 否 | 采用 | REQ-IDN-026 |
| MCSM-379 | 应用市场数据源 | `presetPackAddr` | 是 | 排除 | 明确排除项 | — |
| MCSM-380 | Redis 地址 | `redisUrl`（实验性） | 否 | 不适用 | Servitium 使用 SQLite + 进程内缓存 | — |
| MCSM-381 | 商业模式 | `businessMode`、`businessId`、`panelId`、`registerCode` | 是 | 排除 | 明确排除项 | — |
| MCSM-382 | TLS 开关与证书路径 | `ssl`、`sslPemPath`、`sslKeyPath` | 否 | 采用 | REQ-USR-058 |
| MCSM-383 | 侧栏位置 | 主题条目 `sidebarPosition ∈ {left, right}` | 否 | 采用 | REQ-USR-044 |
| MCSM-384 | 页面标题 | 主题条目 `pageTitle` | 否 | 采用 | REQ-USR-044 |
| MCSM-385 | Logo 图片 | 主题条目 `logoImage`，支持上传 | 否 | 采用 | REQ-USR-044 |
| MCSM-386 | 背景图片 | 主题条目 `backgroundImage`，支持上传 | 否 | 采用 | REQ-USR-044 |
| MCSM-387 | 资源上传 | `POST /overview/upload_assets` | 否 | 采用 | REQ-USR-059 |
| MCSM-388 | 布局读取/保存/重置 | `GET|POST|DELETE /overview/layout` | 否 | 采用 | REQ-USR-040 |
| MCSM-389 | 商业模式刷新 | `POST /overview/refresh_business_mode` | 是 | 排除 | — | — |
| MCSM-390 | 关于页外链 | GitHub / Discord / 官网 / 文档 / 报告缺陷 | 否 | 适配 | Servitium 保留仓库与文档链接 | REQ-USR-060 |
| MCSM-391 | 赞助页 | 捐赠入口 | 否 | 排除 | — | — |

## 15. 容器与镜像

`[SOURCE]` `panel/src/app/routers/environment_router.ts`、`daemon/src/routers/environment_router.ts`、`frontend/src/widgets/imageManager/**`

| 编号 | 功能 | 分类 | 理由 |
| --- | --- | --- | --- |
| MCSM-400 | 镜像列表 / 新建 / 删除 | 不适用 | 无 Docker |
| MCSM-401 | 容器列表 | 不适用 | 同上 |
| MCSM-402 | 网络模式列表 | 不适用 | 同上 |
| MCSM-403 | 构建进度 | 不适用 | 同上 |
| MCSM-404 | 镜像平台查询 / DockerHub 平台查询 | 不适用（且依赖厂商） | 同上 |
| MCSM-405 | Dockerfile 表单 | 不适用 | 同上 |
| MCSM-406 | 容器资源限制（CPU/内存/交换/磁盘/IO/GPU/能力/设备/端口/卷/标签/网络别名/特权模式） | 不适用 | 同上 |

## 16. 其他

| 编号 | 功能 | 说明 | 依赖厂商 | 分类 | 理由 |
| --- | --- | --- | --- | --- | --- |
| MCSM-420 | 快速开始向导 | 引导选择应用类型与创建方式 | 部分是 | 适配 | Servitium 保留「新建 / 上传归档 / 附加目录」三条路径，去掉市场分支 |
| MCSM-421 | Minecraft 预设页 | 从预设包快速部署 | 是 | 排除 | 依赖厂商预设源 |
| MCSM-422 | 应用市场浏览与安装 | — | 是 | 排除 | 明确排除项 |
| MCSM-423 | 市场模板编辑器 | — | 是 | 排除 | 同上 |
| MCSM-424 | 商城 / 卡密 | — | 是 | 排除 | 同上 |
| MCSM-425 | OpenFRP 内网穿透 | `extraServiceConfig.openFrpTunnelId/Token` | 是 | 排除 | 明确排除项 |
| MCSM-426 | MCDReforged 支持 | `daemon/src/service/mcdr_service.ts` | 否 | 排除 | 明确排除项 |
| MCSM-427 | Steam / SteamCMD | 创建向导中的安装命令模板 | 否 | 排除 | 明确排除项 |
| MCSM-428 | 通用进程托管（`universal` 类型） | 托管任意可执行程序 | 否 | 排除 | Servitium 仅托管 Minecraft Java 服务端 |
| MCSM-429 | Minecraft 基岩版 | `minecraft/bedrock` 类型 | 否 | 排除 | 明确排除项 |
| MCSM-430 | 多语言（12 种） | 语言包 | 否 | 适配 | Servitium 首发 `zh-Hans` 与 `en-US` |
| MCSM-431 | 版本适配器 | `version_adapter.ts` 处理配置结构升级 | 否 | 采用 | Servitium 用 EF Core 迁移 + 配置版本号 |
| MCSM-432 | 磁盘配额服务 | `disk_limit_service.ts` | 否 | 适配 | Servitium 提供磁盘空间预检，不做强制配额 |
| MCSM-433 | 网络限速服务 | `network_limit_service.ts` | 否 | 采用 | 见 MCSM-080/081 |
| MCSM-434 | 接管已有容器 | `takeover_container.ts` | 否 | 不适用 | 无 Docker |
| MCSM-435 | 自定义开放页 | `/_open_page` 提供无权限约束的自定义页面 | 否 | 适配 | Servitium 保留为「公开信息页」，只允许静态卡片 |
| MCSM-436 | 桌面客户端 | 无 | 否 | 不适用 | Servitium **新增** WinUI 3 桌面端，见 REQ-DSK-* |
| MCSM-437 | HMCL 内容分发 | 无 | 否 | 不适用 | Servitium **新增**，见 REQ-HMC-* |

## 17. 统计

| 类别 | 数量 |
| --- | --- |
| 清单条目总数 | 304（MCSM-001 … MCSM-437，非连续编号） |
| 前端路由 | 27 |
| 默认布局页面条目 | 24 |
| 前端组件（页面级 + 对话框级） | 76 |
| 面板 HTTP 端点 | 89 |
| 节点 Socket 事件 | 51 |
| **采用** | 195 |
| **适配** | 46 |
| **排除** | 34 |
| **不适用** | 29 |

分类明细见各章「分类」列。Servitium 新增能力（MCSManager 中不存在）另见 `docs/product/functional-specification.md`：Owner 角色、Disabled 角色、Java 系统探测、Job Object 进程树、无损配置往返、配置备份与原子保存、计划任务启停与历史与重叠策略、安全审计分离、终端会话导出、桌面客户端、HMCL 分发。
