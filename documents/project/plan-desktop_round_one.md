# 桌面重写首轮计划

## 目标

先解除依赖审计阻断并形成依赖检查点，再按用户认可的首轮方案，以手写 XAML 与可测试的 ViewModel 重写外壳、登录、实例工作区和设置。节点、分发、用户、活动沿用现有引擎；第二轮与交付不自动开始。

## 范围

### 三处主要风险

1. Windows App SDK 跨主版本升级影响标题栏、窗口、WebView2 和托盘：依赖检查点先完整构建/测试，并在自有进程中验证启动；最终冻结候选复查深浅色、窗口宽度与键盘。
2. 新旧页面并存导致导航/返回/连接切换丢失选中资源或认证状态：以实例为默认入口，明确旧 PG 到新标签的映射；第二轮入口保持原授权与操作，测试切换和返回。
3. 手写界面遗漏权限或表单校验：复用现有客户端/Server 安全边界，ViewModel 显式映射角色、授权实例、状态和命令；补充负向权限与无效输入测试，不扩大后端能力。

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

契约编号保持现有含义；方案中的 PG-015/016/017 与当前编号有交叉，按页面职责落实，不改通信协议和权限。

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

### 能力边界

- InstanceDto 没有实例游戏版本/加载器的可靠字段：不从名称猜测；无元数据时明确未提供，不能照抄原型的示例版本。
- 指标当前提供采样值；未发现实例历史序列接口前不画虚构 CPU/内存曲线。
- NodeDto/NodeDetailsDto 没有独立主机名；仅展示真实节点名和已配置对外地址，不把控制平面主机名冒充节点主机名。运行时玩家查询可返回服务端版本，未运行或没有查询结果时不猜版本/加载器。
- 玩家名单、上限、自动重启次数、输入/输出编码、URL 下载、整合内容同步已有部分真实契约；依实际返回/权限显示，不将示意数据填入生产界面。后续实证发现不可达或缺少能力时在本节与结果中逐项收敛。
- 全局搜索只索引当前可访问的实例、当前实例文件和现有命令，不增加后端跨节点全文检索。
- 打开工作目录只在可证明为本机路径时调用本机资源管理器；远程节点目录使用实例文件标签，不能在客户端误开同名本地目录。
- 不新增通知/托盘、备份、身份或调度能力；只重排现有设置。不能执行的操作不保留假按钮，原因和待办如实记录。

### 并存导航

实例为统一默认页；PG-011 与 PG-012～018 深链进入同一工作区并选中对应标签，PG-019 打开新建流程。PG-040 不再有仪表盘入口，汇总并入实例列表。节点 PG-020～022、分发 PG-060～063、用户 PG-030/031、活动 PG-042～044 仍由原 ProductPageFactory/ContractPage 呈现，经新导航进入；只在多账户并具权限时显示用户入口。设置整合 PG-032/041 和本机偏好。新页面不继承 ContractPage，也不按字段列表生成控件。

## 步骤

1. 完成：计划、风险、官方版本核对；完整依赖/工具链升级、锁定还原/构建/319 项现有测试与审计通过；原生桌面启动截图。本页随第一条 Update 提交并正常推送后进入下一步。
2. 待办：导航外壳与标题栏，接通第二轮页面并验证导航/返回/连接切换。
3. 待办：初始化、登录、两步验证页面及状态/校验测试。
4. 待办：实例列表、详情抬头、生命周期命令及新建流程。
5. 待办：控制台、文件、配置、模组、计划任务、实例设置六个手写标签与测试。
6. 待办：分类设置页，双语资源与契约支持清单。
7. 待办：冻结候选全量还原/构建/测试，自有 Server/Agent/合成实例截图、深浅/宽窄/键盘自查，更新使用说明/人工验收清单，第二条 Update 提交推送后停止。

## 结果

依赖步骤完成，界面尚未重写。原 b625264 可运行的五组后端测试逐项一致；Client/Desktop 恢复后基线分别为 15/38 项，合计 319 项通过且无跳过，完整构建零警告/错误，审计无已知漏洞。解除阻断后的编译暴露了上轮把 ServitiumApiException 随无引用测试客户端一起移出的错误，现将仍使用的异常恢复为独立类型，旧客户端不恢复；既有 Problem Details 测试覆盖稳定字段。SDK 启动验证采用原生 apphost，避免用 dotnet 托管入口造成 WinUI 应用资源路径错误。截图与运行证据仅放仓库外，不触碰已有服务或真实数据。
