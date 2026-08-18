# 验收矩阵

本文件是**可持续更新**的动态记录，由后续编码 Agent 在验收过程中维护。

它只记录事实、状态与证据，**不能用于绕过受保护规格**。若发现验收项本身有问题，走 `docs/records/change-requests.md` 流程。

## 1. 使用方式

| 列 | 说明 |
| --- | --- |
| 验收项 | 来自 `backend-acceptance.md`、`frontend-acceptance.md`、`user-testing-procedure.md` 的编号 |
| 阶段 | 该项归属的开发阶段 |
| 状态 | `未开始` / `进行中` / `通过` / `失败` / `阻塞` |
| 证据 | 命令、测试编号、产物路径、日期 |
| 备注 | 失败原因、阻塞说明、关联的问题编号 |

状态为 `阻塞` 时必须在备注中写明阻塞类型（权限 / 环境 / 规格）与已提交的请求。

日期使用绝对格式 `YYYY-MM-DD`。

本矩阵把四层证据分开解释：`代码存在` 只说明生产路径已实现；`自动测试通过` 只说明列出的当前环境测试；`阶段 Gate 通过` 必须满足对应受保护验收文件的全部条件；`用户验收通过` 只能由用户明确确认。表内“进行中”不得解释为后两层已经通过。

## 2. 后端验收（阶段 18，依据 `backend-acceptance.md`）

| 验收项 | 阶段 | 状态 | 证据 | 备注 |
| --- | --- | --- | --- | --- |
| BA-A01 … BA-A07 | 18 | 进行中 | Release 构建 0 警告；`eng/Generate.ps1 -Verify`；最终连续验证 `validate-20260804-151435/`、`151831/`、`152227/`；覆盖率整体 70.85% | 代码与 Development 检查可运行；未执行阶段 18 `BackendGate`，不得据此标记通过 |
| BA-B01 … BA-B10 | 11、18 | 进行中 | Domain 41、Application 11；UNT-100 … UNT-550、ARC 架构测试 | 领域、用例、授权、游标和架构自动测试已通过；阶段 18 正式复跑未完成 |
| BA-C01 … BA-C06 | 12、16、18 | 进行中 | `SecurityAndPersistenceTests`、`RetentionMaintenanceTests`、`DatabaseBackupTests`、INT-1100、INT-1120、INT-1180、INT-1190 | 迁移、SQLite、保留和在线备份生产代码存在并有隔离自动测试；容量并发与 Gate 未完成 |
| BA-D01 … BA-D11 | 12、18 | 进行中 | UNT-220 … UNT-222、`IdentityEndpointTests`、`OidcDiscoveryEndpointTests`、SEC-003 … SEC-044 | 密码/TOTP/API Key/OIDC/本机换票/限流/CSRF/统一授权和撤销传播代码存在并自动测试；Windows ACL 实机与 Gate 未完成 |
| BA-E01 … BA-E09 | 13、18 | 进行中 | `NodeControlStoreTests`、`NodeEndpointTests`、`ControlPlaneEndToEndTests`、INT-300 … INT-450、SEC-300 … SEC-320 | 节点注册、协议、心跳、对账、轮换、吊销、移除和 Agent 二次校验代码存在并合成测试；平台矩阵未完成 |
| BA-F01 … BA-F04 | 13、18 | 进行中 | `JavaRuntimeRegistryTests`、`PathAndArchiveSecurityTests`、INT-350、INT-370、INT-430、SEC-200 | 探测、登记、归档导入、解析和安全删除代码存在并使用合成 Java/归档测试；两个 Windows 平台未完成 |
| BA-G01 … BA-G19 | 13、14、18 | 进行中 | `InstanceRegistryTests`、`InstanceLifecycleTests`、`ControlPlaneEndToEndTests`、INT-390 … INT-690、SEC-140 … SEC-320 | 三种创建、批量、生命周期、EULA、RCON/状态和进程树生产代码存在并自动测试；真实目录、服务关机与平台矩阵未完成 |
| BA-H01 … BA-H08 | 14、18 | 进行中 | `InstanceLifecycleTests`、`MinecraftProtocolClientTests`、`CoreEndpointWorkflowTests`、INT-500 … INT-700 | ConPTY/管道、编码、缓冲、终端流、日志查询和授权关闭路径有生产实现与合成测试；平台矩阵未完成 |
| BA-I01 … BA-I16 | 15、18 | 进行中 | `InstanceFileServiceTests`、`AgentUploadSessionServiceTests`、`AgentFileTaskCoordinatorTests`、INT-710 … INT-990、SEC-140 … SEC-193 | CRUD、批量、续传、Range、URL、归档、文本、配额、路径与 SSRF 代码存在并自动测试；ACL 实机和真实编码样本未完成 |
| BA-J01 … BA-J06 | 15、18 | 进行中 | `InstanceConfigurationServiceTests`、`PreservingPropertiesDocumentTests`、`PreservingStructuredDocumentTests`、INT-890 … INT-940、UNT-340 | properties/JSON/YAML/TOML 保留结构、校验、备份恢复与 schema 代码存在并以合成夹具测试；受保护验收要求的真实样本未执行 |
| BA-K01 … BA-K04 | 15、18 | 进行中 | `InstanceModServiceTests`、`ControlPlaneEndToEndTests`、INT-950 … INT-970 | 元数据降级、启停、上传、删除和运行中拒绝代码存在并自动测试；真实模组格式矩阵未执行 |
| BA-L01 … BA-L07 | 16、18 | 进行中 | `LocalSchedulerTests`、`TriggerCalculatorTests`、`ControlPlaneEndToEndTests`、UNT-400、INT-1000 … INT-1070 | CRUD、三类触发、六类动作、重叠、历史、本地自治和上限代码存在并自动测试；服务环境恢复测试未完成 |
| BA-M01 … BA-M11 | 16、18 | 进行中 | `AuditEndpointTests`、`SystemSettingsTests`、`DashboardLayoutTests`、`DatabaseBackupTests`、INT-1080 … INT-1200 | 用户治理、双日志、设置、资源消毒、OIDC/TLS 检查、布局、诊断、监听安全生产代码存在并自动测试；完整 Gate 未完成 |
| BA-N01 … BA-N18 | 17、18 | 进行中 | `HmclDistributionTests`、`HmclDistributionAlgorithmTests`、`AgentHmclDistributionServiceTests`、INT-1300 … INT-1480、SEC-165 … SEC-281 | 合成 Prism/HMCL 归档、候选过滤、不可变 Release、原子发布/回滚、保留/GC、私有令牌、统计和协议已自动测试 |
| BA-N19 | 17、18 | 未开始 | | 真实 HMCL 客户端协议级验证按用户要求未执行 |
| BA-O01 … BA-O08 | 18 | 未开始 | | 正式容量与 30 分钟稳定/恢复测试未执行，不能用单实例合成测试替代 |
| BA-P01、BA-P03 | 18 | 进行中 | INT-1492、INT-1493、`ControlPlaneEndToEndTests`、`IdentityEndpointTests` | 合成管理和权限纵向路径已执行；正式端到端 Gate 未执行 |
| BA-P02 | 18 | 未开始 | | 含真实 HMCL 客户端，按用户要求未执行 |
| BA-Q01 … BA-Q05 | 18 | 未开始 | | 平台矩阵，可能需要用户提供第二个 Windows 版本 |
| BA-R01 … BA-R05 | 18 | 未开始 | | 需要管理员权限 |
| BA-S01 … BA-S04 | 18 | 进行中 | 需求矩阵逐项映射；60 个错误码；安全负向测试；PB-022 … PB-032 已关闭 | 最终完整验证后复核；阶段 18 Gate 仍未执行 |

## 3. 前端验收（阶段 23，依据 `frontend-acceptance.md`）

当前用户明确允许在阶段 18 前完成正式 WinUI 3 Desktop；30 页 Desktop、共享 Client 和操作链已写入生产代码，但用户真实启动截图发现 PB-039 响应式布局阻断，阶段 22 不得标记为代码完成或可供核心功能测试。`eng/Check-DesktopUi.ps1` 已在隔离 Server 上提供当前 150% DPI 的 compact/normal/wide UIA 与截图证据，完整 DPI 矩阵和逐页人工视觉验收仍未完成。逐页证据见 `docs/records/desktop-page-gap-audit.md`。React Web 按本轮边界未开始，因此涉及双端或 Web 的验收不能通过；下列“进行中”只代表代码、有限自动 UI 检查和局部自动测试证据。

| 验收项 | 阶段 | 状态 | 证据 | 备注 |
| --- | --- | --- | --- | --- |
| FA-A01 … FA-A09 | 21、22、23 | 进行中 | Desktop Release x64 0 警告；生成 30 页/776 双语键/181 Client 操作；Desktop 38/38、Client ContractTests 15/15；`eng/Check-DesktopUi.ps1` 报告 `artifacts/test-results/desktop-ui/20260804-163934/`（当前 150% DPI，compact/normal/wide） | PB-039 仍开放；100%、125%、200% DPI、完整逐页视觉、Web、FrontendGate 和禁止项全套未执行 |
| FA-B01 … FA-B08 | 21、22、23 | 进行中 | `ui-contract.json`、`ProductPages.cs`、`ContractPage.cs`、`desktop-page-gap-audit.md`、Desktop 契约测试 | Desktop 30 页字段、动作和状态代码存在；Web 与跨端一致性未执行 |
| FA-C01 … FA-C05 | 21、22 | 进行中 | `ContractPage.cs`、`PageModels.cs`、`SessionService.cs`、Desktop 38/38 | Desktop 的加载、空、错误、权限和离线状态代码存在；视觉/用户验收与 Web 未执行 |
| FA-D01 … FA-D10 | 21、22 | 进行中 | `PageActionDispatcher*.cs`、`UserInteractionService.cs`、Desktop 38/38 | Desktop 确认、批量、标签、节点选择、面包屑、多标签和长操作代码存在；Web 未开始 |
| FA-E01 … FA-E10 | 21、22 | 进行中 | `TerminalWindowService.cs`、WebAssetsSource/terminal、Client 实时连接、Agent 终端集成测试 | Desktop 终端代码和后端合成链路存在；正式可视、性能、撤权和 Web 验收未执行 |
| FA-F01 … FA-F10 | 21、22 | 进行中 | `FileManagerModels.cs`、`PageActionDispatcher.Actions.cs`、Client 文件方法、Desktop/Client/Agent 文件测试 | Desktop 文件操作、原子下载、上传、预览和确认代码存在；真实文件、拖放视觉与 Web 未执行 |
| FA-G01 … FA-G06 | 21、22 | 进行中 | Desktop 配置页面动作、Client 配置方法、INT-890 … INT-940 | Desktop 配置识别、编辑、错误和备份动作存在；真实格式及 Web 验收未执行 |
| FA-H01 … FA-H08 | 21、22 | 进行中 | Desktop 30 页动作、Client 治理/节点/实例方法、INT-950 … INT-1200、INT-1492、INT-1493 | Desktop 功能页生产代码存在；视觉/用户验证与 Web 未执行 |
| FA-I01 … FA-I09 | 21、22 | 进行中 | PG-061 … PG-063、Client Distribution、Desktop Prism 测试、INT-1300 … INT-1480 | Desktop HMCL 全流程代码存在并使用合成数据测试；真实 HMCL 和 Web 未执行 |
| FA-J01 … FA-J14 | 22 | 进行中 | `App.xaml.cs`、`MainWindow.xaml.cs`、Desktop Services、Desktop 38/38；`eng/Check-DesktopUi.ps1` 当前 150% DPI 的 compact/normal/wide UIA 与截图；隔离进程自检退出码 0 | PB-039 仍开放；100%、125%、200% DPI、30 页逐页实际启动、正式可视、性能、辅助功能和用户验收未执行 |
| FA-K01 | 23 | 未开始 | | |
| FA-L01 … FA-L08 | 23 | 未开始 | | |
| FA-M01 … FA-M09 | 23 | 未开始 | | |
| FA-N01 … FA-N05 | 23 | 进行中 | zh-Hans/en-US 各 776 键；`LocalizationService.cs`（含未打包 JSON 回退）；`Generate.ps1 -Verify`；Desktop 38/38 | 自动键集合和稳定值映射已验证；逐页人工语言验收未执行 |
| FA-O01 … FA-O06 | 21、22、23 | 进行中 | `DesktopPageAuthorization.cs`、`WindowsCredentialSessionTokenStore.cs`、`RestrictedWebViewHost.cs`、Desktop 权限矩阵 | Desktop 安全生产代码与局部自动测试存在；Web、正式安全走查和用户验收未执行 |
| FA-P01 … FA-P07 | 23 | 未开始 | | |

## 4. 用户验收（阶段 24，依据 `user-testing-procedure.md`）

四项真实样本协作测试尚未执行，且本轮交接不是阶段 24 正式验收；所有用户验收项和最终确认继续保持未开始。

| 验收项 | 状态 | 用户确认日期 | 备注 |
| --- | --- | --- | --- |
| U-01 … U-07 初始化与登录 | 未开始 | | |
| U-10 … U-15 节点 | 未开始 | | |
| U-20 … U-23 Java 运行时 | 未开始 | | |
| U-30 … U-34 实例创建 | 未开始 | | |
| U-40 … U-48 运行与控制台 | 未开始 | | |
| U-50 … U-61 文件管理 | 未开始 | | |
| U-70 … U-75 配置 | 未开始 | | |
| U-80 … U-84 模组 | 未开始 | | |
| U-90 … U-95 计划任务 | 未开始 | | |
| U-100 … U-105 用户与权限 | 未开始 | | |
| U-110 … U-116 仪表盘与设置 | 未开始 | | |
| U-120 … U-130 HMCL 分发 | 未开始 | | |
| U-140 … U-152 Desktop 专项 | 未开始 | | |
| U-160 … U-162 双端一致性 | 未开始 | | |
| **用户最终验收确认** | 未开始 | | 用户明确确认前不得标记通过 |

## 5. 门禁状态

| 门禁 | 条件 | 状态 | 日期 |
| --- | --- | --- | --- |
| 阶段 18 硬门禁 | 第 2 节全部通过 | 未通过 | |
| 阶段 23 完成 | 第 3 节全部通过 | 未通过 | |
| 阶段 24 硬门禁 | 第 4 节全部通过且用户明确确认 | 未通过 | |
| 交付启动 | 阶段 24 通过 **且** 用户明确要求启动交付 | 未满足 | |

## 6. 更新记录

| 日期 | 更新内容 | 更新者 |
| --- | --- | --- |
| 2026-08-02 | 建立矩阵骨架（阶段 00） | 文档基线 |
| 2026-08-03 | 登记阶段 10 三次开发配置验证证据；阶段 18 验收状态保持未开始 | 编码 Agent |
| 2026-08-03 | 登记阶段 11 领域与架构测试证据；跨阶段后端验收组保持进行中 | 编码 Agent |
| 2026-08-03 | 登记阶段 12–17 核心链路的合成自动化证据与受限 WinUI 启动探针；正式后端、前端和用户验收均未标记通过 | 编码 Agent |
| 2026-08-04 | 区分代码、自动测试、正式 Gate 与用户证据；登记完整后端、Client 和 30 页 Desktop 当前实现，真实样本、容量、平台、Web 与正式 Gate 保持未通过 | 编码 Agent |
| 2026-08-04 | 追加 PB-037、PB-038 修复后的 Desktop 38/38 与进程自检退出码 0；登记最终连续验证 `validate-20260804-151435/`、`151831/`、`152227/`；阶段 18、23、24 仍未通过 | 编码 Agent |
