# 桌面重写与后端能力补齐计划

## 目标

按用户 2026-10-22 的决定，将两轮合为本轮：先收尾现有手写页面并推送检查点，再补齐原型展示的后端能力，完成节点、分发、用户、活动页面并删除表单引擎。依赖检查点已经完成，不重复升级。Web、legacy 与交付继续暂停；没有用户验收结论。

## 范围

### 三处主要风险

1. 新字段、历史查询和实时订阅绕过现有授权：Server 继续使用单一授权入口，Agent 独立校验；用普通账户、跨实例/跨节点请求与事件订阅的负向测试证明隔离，CSRF 不变。先修复普通账户错误订阅 global 的实际缺陷。
2. 历史序列和下载统计持续增长：沿用存储、分页和保留期限机制，明确采样粒度与清理；测试边界、时序、过期清理和空窗口，不在 UI 内伪造历史。
3. 删除引擎破坏深链、返回、连接切换或低频动作：先完成四组手写页面及完整动作映射，再迁移工厂和深链，确认零引用后将引擎移出仓库；保留契约语义校验与相关行为回归。

### 依赖与工具链

版本唯一来源仍为 global.json 和 Directory.Packages.props；本表记录本次变更，不另作构建输入。

| 组成 | 本次动作 |
| --- | --- |
| 当前为 10.0.10 的 Microsoft.AspNetCore.OpenApi/Mvc.Testing/SignalR.Client、EF Core Design/Sqlite、Extensions Hosting/WindowsServices、System.Security.Cryptography.ProtectedData | 统一升级到用户指定修复版 10.0.13，重新生成所有相关锁文件，保留审计与警告级别 |
| .NET SDK | 官方免安装 10.0.402，含 .NET/ASP.NET Core 10.0.13；global.json 固定，Local.props 指向仓库外 SDK，不改系统环境 |
| Windows App SDK | 当前官方稳定版 2.5.1；相关 Windows SDK BuildTools 同步到稳定版 10.0.28000.2705；不安装系统运行时或安装包 |
| SettingsCard/SettingsExpander | 引入 CommunityToolkit.WinUI.Controls.SettingsControls 稳定版 8.2.251219；沿用 CommunityToolkit.Mvvm |
| 被中央锁定的传递依赖 | 按 10.0.13 实际依赖最低要求同步 Microsoft.OpenApi 到 2.12.2、SQLitePCLRaw.lib.e_sqlite3 到 2.1.13；不允许包降级 |

来源：[微软 .NET 下载](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)、[Windows App SDK 稳定通道](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)、[Windows 构建工具](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools)、[SettingsControls](https://www.nuget.org/packages/CommunityToolkit.WinUI.Controls.SettingsControls)。实施前已核实，下载后核对官方 SHA-512。

### 页面与现有契约

契约编号保持现有含义；方案中的 PG-015/016/017 与当前编号有交叉，按页面职责落实；新增能力以兼容字段扩展，授权与安全不变量保持不变。

| 页面/视图 | 现有字段与动作 | 新呈现 |
| --- | --- | --- |
| 外壳 | Session/Principal、ServerProfile、DesktopSettings；ListUsers/GetUserOverview、ListOperations、ListInstances、文件搜索和已有快捷命令 | 紧凑 NavigationView、不同图标；连接切换、Ctrl+K 搜索、长操作入口；无认证时隐藏导航 |
| PG-001 初始化 | user-name/password/confirm-password/language；complete-setup | 居中单栏，末尾主按钮，密码一致和初始化前置状态 |
| PG-002 登录 | user-name/password；sign-in/external-sign-in | 居中单栏，错误原因/重试，不泄露凭证 |
| PG-003 两步验证 | code/recovery-code；verify/use-recovery-code | 居中单栏，可切换恢复码 |
| PG-010/011 实例列表 | query/node/status/tags；create/start/stop/restart/force-stop/unregister 或授权实例 open | 合并工作区；节点分组、原生多选、汇总/筛选；普通用户仅看到授权集合 |
| PG-012 控制台 | command；start/stop/restart/force-stop/send-command/sync-content/export-session-log/open-log-files/open-settings；运行状态、指标、玩家与详细配置 | 工作区标题/CommandBar、WebView2+xterm、命令与快捷命令、检查栏及离线/崩溃原因 |
| PG-013 文件 | path/search/sort/descending/collision-policy/upload/url/target-file/content/encoding/entry-name-encoding/extract-encoding/overwrite；refresh/new-tab/close-tab/create-file/create-directory/upload/download-from-url/download/preview/edit-text/save-text/rename/copy/cut/paste/move/delete/compress/extract | BreadcrumbBar、选择工具栏、表格、拖入上传、实例目录边界说明；文件占用错误保持真实 |
| PG-014/015 配置 | query/format/content；refresh/open/open-file-manager/view-backups/save/discard/toggle-view/restore；ConfigurationSchema/Details/Revision | 左侧文件、结构化 SettingsCard/原文、未保存摘要与 Ctrl+S、离开确认，修订仅内部用于并发 |
| PG-016 模组 | query/type/enabled；refresh/upload/details/open-configuration/enable/disable/delete | 名称/版本/开关表格，重新扫描和上传，运行中限制说明 |
| PG-017 计划任务 | name/trigger/actions/remaining-runs/overlap-policy；refresh/create/edit/save/view-history/run-now/enable/disable/delete | 任务表格、右侧编辑面板及历史；不添加新调度语义 |
| PG-018 实例设置 | name/tags/notes/working-directory/java-runtime/minimum-heap/maximum-heap/jvm-arguments/target-kind/launch/server-arguments/environment-variables/advanced-mode/advanced-arguments/run-credential-alias/stop-command/stop-timeout/primary-port/auto-start/auto-start-delay/auto-restart/maximum-restart-attempts/restart-interval/restart-reset/terminal-conpty/terminal-columns/terminal-rows/terminal-input-encoding/terminal-output-encoding/terminal-newline/terminal-ansi/rcon-enabled/rcon-port/rcon-password/query-enabled/query-host/query-port/update-kind/update-source/update-target/update-channel/shortcut-commands；save/unregister/delete-working-directory | SettingsCard 分组，参数展开和预览，危险操作确认；非管理员不获得危险设置权限 |
| PG-019 新建实例 | source-kind/name/tags/notes/node/absolute-path/archive/java-runtime/minimum-heap/maximum-heap/jvm-arguments/target-kind/launch/server-arguments/environment-variables/advanced-mode/advanced-arguments/stop-command/stop-timeout；create-empty/import-server-archive/attach-directory | 三步 ContentDialog：来源与位置、启动、检查/执行；只使用合成样本验证 |
| 本应用设置 | 已有 Language/Theme/CloseToTray/TerminalFontSize/通知/ServerProfiles | 外观与语言、终端、通知与托盘、服务器连接；标明仅此电脑生效 |
| PG-041 服务器设置 | language/listen/tls/trusted-proxy/oidc/retention 与现有 typed settings；save/test/export-diagnostics/backup-database/reset-dashboard | 常规、安全、身份登录、关于与备份，SettingsCard；标明影响所有连接者，隐藏内部键/修订 |
| PG-032 账户 | display-name/language/current-password/new-password；save/change-password/configure-totp/remove-totp/create-api-key/revoke-api-key/bind-oidc/unbind-oidc/revoke-session | 设置里的账户与安全，手写 XAML；危险身份动作确认 |

| PG-020～022 节点 | 节点分页/详情/配置、重连/凭据轮换/移除、Java 登记与扫描、指标查询 | 列表加详情、概况/实例/Java/设置标签，危险动作确认 |
| PG-060～063 分发 | Channel、Archive、不可变 Release、显式发布/回滚、下载统计 | 通道工作区、概况/历史/设置、分步发布，真实七天统计 |
| PG-030/031 用户 | 用户概况/创建/资料/角色/禁用/实例 grants、身份与所有权动作 | 多账户列表加详情；单账户说明和添加入口；授权在详情内 |
| PG-042～044 活动 | 长操作分页/详情/取消、活动日志/导出、只追加安全审计 | 任务/操作日志/安全审计标签，标题栏数量进入任务 |

### 后端能力与实现位置

先审查已有能力，已有端到端实现只补呈现与缺失验证，不重复造后端。下列位置为实施起点；具体新增字段与迁移在相应实现时确定并同步。

| 能力 | 当前证据/缺口 | 实现位置与验证 |
| --- | --- | --- |
| 可靠游戏版本、加载器 | 列表 DTO 缺少可靠字段，不从实例名猜测 | Contracts/Instances、Agent 实例存储与启动元数据、Server 节点镜像；增加可空字段及来源，合成 JAR/元数据验证未知与冲突 |
| 实例 CPU/内存历史 | 现有 InstanceTelemetryPublisher 提供当前采样 | Agent/Metrics、Agent/Storage、节点事件与 Server 查询、Client；沿用指标保留设置，测试采样、分页、清理和授权 |
| 文件占用与运行时写入 | 条目缺少占用状态，Agent 写入要求停止 | Contracts/Files、Agent 文件枚举与现有路径校验；区分锁定/未检测到锁/无法判断，使用自有句柄负向验证。瞬时探测不能保证多步写入安全时保留停止后写入，并说明原因 |
| 玩家名单、人数上限 | 已有查询能力，列表摘要不足 | Agent 查询/实例镜像、Contracts/Instances、控制台与列表 VM；只投影实际结果，未知不置零，协议夹具验证 |
| 自动重启次数上限 | 已有实例生命周期字段 | 复核 Agent 重启策略、InstanceSettings/Console 映射与边界测试，不引入重复设置 |
| 控制台输入/输出编码 | 已有终端配置字段 | 复核 Agent 终端、Contracts/Terminal、手写设置与真实夹具编码回归 |
| URL 下载 | 已有文件任务与 SSRF 防护 | 复用 Agent 文件任务、Client、Files VM；本机合成 HTTP 源及逐跳/重绑定拒绝回归，不放宽出站策略 |
| 整合内容同步 | 已有 AgentHmclDistributionService/实例更新动作 | 复核分发通道关联、实例命令、真实长操作结果；合成不可变 Release 与 Channel 验证 |
| 节点主机名、系统、版本、本机或远程 | 部分版本与连接元数据已存在，独立主机名不足 | Contracts/Nodes、agent-control.proto、Agent 注册/心跳、Server 节点存储；只用真实报告与传输来源标注，不用于授权 |
| 节点当前指标与 24 小时历史 | 已有 NodeMetricSampler、Server 指标存储与端点 | 复核磁盘/内存容量与 CPU 历史完整性，补缺失契约和保留清理；Nodes VM 绘制真实序列，空窗不补示例点 |
| 节点实例汇总 | 已有节点和实例镜像 | Server 查询/Contracts/Nodes、Nodes VM；汇总与授权过滤一致，跨节点负向回归 |
| 七天同步请求统计 | 已有 HMCL 统计存储，需要核实口径与完整七天桶 | Infrastructure/Distribution、Server/Hmcl、Contracts/Distribution、Client；按实际 HTTP 请求聚合并测试时间边界 |
| 版本历史与回滚 | 已有不可变 Release 和激活记录 | 复用 HmclManagementEndpoints、HmclDistributionStore，必要时扩充摘要；回滚仍为显式原子 Channel 发布并确认 |
| 任务进度、活动与审计 | 已有 OperationDetails、活动与审计查询 | Server/Endpoints、Infrastructure、Contracts/Operations 与 Client；Activity VM 展示真实进度/完成时间/失败原因；安全审计无修改删除路径 |
| 标题栏进行中数量 | 当前从授权后的长操作分页累计 | 复核 Pending/Running 查询和实时刷新、Shell VM 与活动页同源，测试权限与状态转换，不按显示行数猜测总量 |

### 协议与数据库

1. 先列出现有 REST、gRPC 包络与 DTO 的实际字段，新增可空/有默认值的兼容字段，不改变既有授权入口与 CSRF。
2. OpenAPI 由 Server 导出；Client 生成清单、Agent protobuf 定义、资源与协议文档在同一变更中同步，生成文件不手改。
3. 新持久化字段或历史表通过现有 EF Core 设计时工厂生成新迁移，保留已有迁移；用已有结构的合成数据库验证平滑升级、数据保留和回读。
4. 历史查询必须有时间边界、分页与保留期限；统计按实际采样/请求产生，不复制原型示例值。

### 并存与删除顺序

首轮检查点期间，节点、分发、用户与活动仍经新导航进入原页面。之后先接真实后端数据，再逐组迁移手写视图、强类型 ViewModel 与危险确认。PG-011、PG-012～019、PG-020～022、PG-030/031、PG-032/041、PG-042～044、PG-060～063 的深链保持职责映射；PG-040 重定向实例工作区。

四组均可达并验证后，移除 ProductPageFactory 的 ContractPage 创建分支、ContractPage、PageActionDispatcher 系列与仅服务该引擎的页面模型。只退役无引用代码；仍共用的传输、认证、权限与格式化能力保留。删除采用回收站或仓库外保留的方式，不永久删除。最后检索调用方并用语义契约与实际页面操作复核。

## 步骤

1. 完成：66 号依赖检查点已提交并推送；完整还原/构建与当时 319 项现有测试通过，Client/Desktop 恢复基线为 15/38。SDK、包版本与锁文件不重复修改。
2. 完成：首轮收尾已完成，自有环境、TOTP 与事件订阅协议回归、停止图标、全量验证、四组截图与键盘回读均已复查；本次 Update 为首轮检查点。
3. 待办：按能力表逐项补齐后端、契约、迁移和测试，复核授权与保留期限。
4. 待办：把首轮页面原留空数据接入真实结果，维持原有输入、危险操作与安全边界。
5. 待办：按节点、分发、用户、活动的顺序完成手写视图、ViewModel、相关测试和自有环境运行。
6. 待办：迁移全部深链和动作，确认零依赖后移出表单引擎及字符串动作分派代码。
7. 待办：冻结候选完整还原、构建、全部测试、安全与授权回归、全部页面深浅/宽窄/状态截图与键盘自查；同步文档，Update 提交推送，提供一次人工清单后停止。

## 结果

首轮收尾已完成，本次提交形成检查点；当前验证结论见[进度](status-development.md)。合并任务仍有原型后端能力、四类页面、引擎删除和最终冻结验证待完成，用户尚未验收。
