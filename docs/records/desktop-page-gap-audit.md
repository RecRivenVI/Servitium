# Desktop 逐页差异记录

本文件记录 2026-08-04 对正式 WinUI 3 Desktop 的一次性逐页代码核对，以及用户真实启动后暴露的共享响应式布局缺陷。它只说明当前工作树中的代码与自动化证据，不替代阶段 23、阶段 24、辅助功能人工检查、完整 DPI 矩阵或真实样本验收。

## 1. 核对基线与方法

- 基线：`docs/design/information-architecture.md`、`docs/design/desktop-specification.md`、`docs/testing/frontend-acceptance.md`。
- 机器可读契约：`src/Servitium.Contracts/Ui/ui-contract.json`，共 30 个页面。
- 实现入口：`src/Servitium.Desktop/Views/ContractPage.cs`、`ProductPages.cs`、`ViewModels/PageActionDispatcher*.cs`、`MainWindow.xaml.cs`。
- 协议边界：Desktop 只引用 `Servitium.Client` 与 `Servitium.Contracts`；页面操作经生成协议客户端访问 Server。
- 核对维度：路由、权限、区段、字段、动作、加载/空/失败状态、危险确认、本地化、键盘入口、后端接线，以及共享页面容器、字段宽度、导航折叠和标题重排。
- 实际 UI 证据：`eng/Check-DesktopUi.ps1`；隔离 Server 上的最新代表性报告为 `artifacts/test-results/desktop-ui/20260804-163934/`。该报告在当前 Windows 150% DPI 实际启动 compact 560、normal 960、wide 1400 有效宽度，保存三张 PNG，并检查控件边界、可见性、重叠、焦点、可点击性、空初始化拒绝和稳定值泄漏。PB-040 修复后，另在合成 Server `http://127.0.0.1:9201` 恢复会话并用 UIA 检查了 8 个顶层导航页面；这不是 30 页逐页验收。

## 2. 逐页结果

| 页面 | 本轮代码核对结果 | 主要实现证据 | 尚未计为通过的证据 |
| --- | --- | --- | --- |
| PG-001 初始化 | Owner 初始化、密码确认、语言和提交状态已接线；共享布局修复后在隔离 Server 上完成三种窗口宽度 UIA 启动检查 | `ContractPage.cs`、`PageActionDispatcher.Actions.cs`、`MainWindow.xaml.cs`、`eng/Check-DesktopUi.ps1` | 当前实际 DPI 只有 150%；100%、125%、200% DPI、全屏/缩放组合、全新安装和人工界面验收未执行 |
| PG-002 登录 | 密码与 OIDC 登录、提交和重定向状态已接线 | `SessionService.cs`、`PageActionDispatcher.Actions.cs` | 真实 OIDC 提供方矩阵未执行 |
| PG-003 TOTP | TOTP 与恢复码共用受限会话完成链路 | `SessionService.cs`、`PageActionDispatcher.Actions.cs` | 人工键盘与读屏验收未执行 |
| PG-010 实例列表 | 节点/状态/名称/标签过滤及批量生命周期动作已接线 | `PageActionDispatcher.cs`、`PageActionDispatcher.Actions.cs` | 真实多节点数据量验收未执行 |
| PG-011 我的实例 | 仅用户角色导航、授权实例列表与打开动作已接线 | `MainWindow.xaml.cs`、`DesktopPageAuthorization.cs` | 用户验收未执行 |
| PG-012 实例控制台 | 状态、指标、玩家、命令、更新、日志和设置导航已接线 | `PageActionDispatcher.cs`、`TerminalWindowService.cs` | 真实服务端终端观感未执行 |
| PG-013 文件管理 | 多标签、多选、复制/剪切/粘贴、冲突策略、分块上传、临时文件下载、文本/图片预览和任务进度已接线 | `FileManagerModels.cs`、`PageActionDispatcher.Actions.cs`、`ProgressWriteStream` | 真实大文件与人工拖放/辅助功能验收未执行 |
| PG-014 配置列表 | 列表、打开、文件管理与备份导航已接线 | `PageActionDispatcher.cs`、`PageActionDispatcher.Actions.cs` | 真实配置集合验收未执行 |
| PG-015 配置编辑 | 结构化/原始视图、修订冲突、诊断、保存、放弃和备份恢复已接线 | `ContractPage.cs`、`PageActionDispatcher.Actions.cs` | 用户配置样本未执行 |
| PG-016 模组与插件 | 类型/搜索/启用过滤、上传、详情、配置、切换和删除确认已接线 | `PageActionDispatcher.cs`、`PageActionDispatcher.Actions.cs` | 真实 JAR 元数据矩阵未执行 |
| PG-017 计划任务 | 三类触发器、动作链、次数、重叠策略、历史和立即运行已接线 | `PageActionDispatcher.Actions.cs`、`TryParseScheduleTrigger` | 节点长期运行观察未执行 |
| PG-018 实例设置 | 常规、路径、Java、启动、停止、终端、RCON、查询、更新、快捷命令和危险区已接线 | `ui-contract.json`、`PageActionDispatcher.Actions.cs` | 真实实例修改未执行 |
| PG-019 实例创建 | 五步向导覆盖空实例、服务端归档和外部目录；标签、备注、停止字段与所有权语义进入最终确认 | `ContractPage.cs`、`PageActionDispatcher.Actions.cs` | 两项真实实例创建测试未执行 |
| PG-020 节点列表 | 名称和有效期可配置的一次性令牌、地址、复制和 Agent 命令说明已接线 | `NodeRegistrationInstructions.cs`、`UserInteractionService.cs` | 真实 Windows 服务接入未执行 |
| PG-021 节点详情 | 指标、地址映射、目录、配额、终端、关机、计划、来源和凭据动作已接线 | `PageActionDispatcher.cs`、`PageActionDispatcher.Actions.cs` | 管理员服务环境验收未执行 |
| PG-022 Java | 列表、发现、登记、归档导入、使用实例和安全删除已接线 | `PageActionDispatcher.Actions.cs` | 真实 Java 归档和平台矩阵未执行 |
| PG-030 用户 | 搜索、角色过滤、创建/编辑、密码、SID、TOTP、会话、外部身份、启停和删除确认已接线 | `PageActionDispatcher.Actions.cs` | 多管理员并发人工验收未执行 |
| PG-031 用户授权 | 首次安全说明、搜索、状态过滤、多选、批量授权/撤销和实时刷新已接线 | `DesktopSettingsService.cs`、`PageActionDispatcher.cs`、`MainWindow.xaml.cs` | 多会话两秒传播人工观察未执行 |
| PG-032 账户 | 资料、语言、密码、TOTP、API Key、OIDC 绑定和会话撤销已接线 | `PageActionDispatcher.Actions.cs` | 真实 OIDC 与系统通知未执行 |
| PG-040 仪表盘 | 节点、实例、会话、系统、活动、图表和持久布局定制已接线 | `PageActionDispatcher.cs`、`DashboardEmbedHost.cs` | WebView2 图表视觉验收未执行 |
| PG-041 系统设置 | 网络、TLS、代理、OIDC、保留、诊断、备份和布局重置已接线 | `PageActionDispatcher.Actions.cs` | 需要重启或管理员权限的设置未执行 |
| PG-042 活动日志 | 时间、用户、对象类型、资源、动作、结果过滤；加载更多和导出继承当前过滤 | `ActivityFilter`、`ExportActivityAsync` | 大数据量导出人工验收未执行 |
| PG-043 安全审计 | 时间、用户、分类、动作、资源、结果过滤；加载更多和导出继承当前过滤 | `SecurityFilter`、`ExportAuditAsync` | 正式审计覆盖抽查未执行 |
| PG-044 操作中心 | 类型/状态过滤、详情和可取消操作已接线 | `PageActionDispatcher.cs`、`PageActionDispatcher.Actions.cs` | 长时间真实任务人工验收未执行 |
| PG-050 公开下载 | 匿名公开 Channel 列表和下载入口已接线 | `PageActionDispatcher.cs`、`PublicDownloadAsync` | 非回环受信 TLS 环境未执行 |
| PG-051 未找到 | 返回首页动作和匿名访问已接线 | `ProductPages.cs`、`PageActionDispatcher.Actions.cs` | 人工导航验收未执行 |
| PG-060 分发总览 | Channel 创建、可见性、打开、Release 创建入口、编辑和删除已接线 | `PageActionDispatcher.cs`、`PageActionDispatcher.Actions.cs` | 真实 Channel 数据验收未执行 |
| PG-061 Channel | 活跃 Release、历史、统计、客户端地址、私有令牌、发布、回滚和保留设置已接线 | `PageActionDispatcher.cs`、`PageActionDispatcher.Actions.cs` | 真实 HMCL 更新未执行 |
| PG-062 Release 创建 | 普通归档、实例采集和 Prism 三种来源；后端安全预览、排除项、差异、元数据、创建/创建并发布已接线 | `PageActionDispatcher.Actions.cs`、`StagedPrismPreviewDto` | 真实 Prism 与 HMCL 测试未执行 |
| PG-063 Release 详情 | 元数据、清单、文件搜索、完整性、历史、清单下载和回滚已接线 | `PageActionDispatcher.cs`、`PageActionDispatcher.Actions.cs` | 真实大 Release 人工验收未执行 |

## 3. 本轮识别并修正的跨页差异

- Prism 发布动作已从 PG-019 迁移到 PG-062；PG-019 不再要求 Channel，也不再用本地 ZIP 条目统计替代后端检查。
- PG-013 下载改为同目录临时文件完整写入后替换，失败不会删除既有目标文件。
- PG-019 改为正式五步向导，并把停止方式、停止超时、标签、备注和完整启动配置发送到 Server。
- PG-020 不再固定匿名名称和 30 分钟；一次性令牌不会嵌入可复制命令模板。
- PG-031 增加安全说明持久化、多选批量授权/撤销和权限事件刷新。
- PG-042、PG-043 的翻页与导出均继承当前过滤对象。
- 状态、角色、类型、公开性、来源和操作类型采用稳定协议值与本地化显示值分离。
- 页面权限预判现在区分管理员危险设置和精确实例授权；Server 授权仍是最终边界。
- 用户截图暴露的 PB-039 已使阶段 22 保持“部分完成，UI 阻塞”：NavigationView 折叠时隐藏侧栏内部设置控件，展开时通过滚动区域保留可读宽度；页面根容器和卡片改为可拉伸，通用页头在窄宽度改为双行，密码/确认密码/选择控件设定统一可用宽度，初始化动作位于所有者表单内。PB-040 的事件流握手 CSRF 与空可选 GUID 查询参数已修复，Client 15/15；该协议修复不关闭 PB-039。
- `False`、`zh-Hans`、`en-US`、角色/状态/错误码等不再直接作为展示文案；页面与操作详情通过本地化展示模型转换，内部值只保留在请求和状态模型中。

## 4. 结论边界

30 个页面均有当前契约对应的生产页面类、字段、动作和后端分派；共享布局构造已修正并由当前 150% DPI 的代表性启动报告及 8 个顶层导航 UIA 检查提供有限证据，但用户已确认的 PB-039 仍未关闭。当前结论仅为代码核对和有限真实 UI 自动化状态；100%、125%、200% DPI、30 页逐页视觉/交互、阶段 22、阶段 23、阶段 24、真实样本、管理员环境、第二 Windows 平台、读屏/键盘/高对比度及人工视觉验收均未标记通过。
