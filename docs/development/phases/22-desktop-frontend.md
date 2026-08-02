# 阶段 22：Desktop 前端

## 1. 阶段目的

实现 `Servitium.Desktop` 的全部页面与平台专属能力，达到 `docs/design/information-architecture.md` 与 `desktop-specification.md` 定义的规格，且与 Web 保持一致。

## 2. 需求编号

`REQ-DSK-001` … `REQ-DSK-050`，以及全部界面相关需求的 Desktop 侧实现。

## 3. 前置条件

* 阶段 21 完成条件全部满足。

## 4. 范围

* `information-architecture.md` 第 1 节的全部页面的 Desktop 实现；
* 单实例与应用生命周期；
* 本机换票与回退登录；
* Mica、自定义标题栏、主题、窗口几何记忆；
* `NavigationView` 与返回栈；
* 系统托盘（图标状态、菜单、实例快捷操作、暂停通知）；
* 系统通知（类别、聚合、深链接、排队）；
* 受限 WebView2 承载的终端与编辑器；
* 独立终端窗口；
* 从资源管理器拖放上传；
* 多服务器配置与切换；
* 本地设置与日志。

## 5. 非目标

* 不做双端一致性最终比对（阶段 23）；
* 不做用户验收（阶段 24）；
* 不实现安装器（交付阶段）。

## 6. 需要创建的项目或目录

在 `src/Servitium.Desktop/` 下补齐：

```text
App/          单实例、生命周期、主题、Mica、深链接
Shell/        NavigationView、标题栏、返回栈、全局提示
Views/        与页面编号一一对应的页面
ViewModels/   CommunityToolkit.Mvvm
Services/     Session、Notification、Tray、DeepLink、Settings、ServerProfiles、WebViewHost
Resources/    生成的 .resw
```

## 7. 需要实现的类型或模块

### 7.1 页面

与 Web 页面一一对应。每页必须：

* 结构与 `information-architecture.md` 中该页的规格一致；
* 使用 WinUI 3 原生控件按 `shared-ui-specification.md` 的语义实现；
* 为契约中登记的每个区块、字段、动作设置 `AutomationId`（供一致性测试采集）；
* 文案来自生成的 `.resw`；
* 数据经 `Servitium.Client`。

### 7.2 平台专属服务

| 服务 | 职责 |
| --- | --- |
| `SingleInstanceService` | 应用实例重定向、激活既有窗口 |
| `SessionService` | 本机换票、续期、回退登录、多服务器切换 |
| `TrayService` | 托盘图标状态、菜单构建、实例快捷操作、暂停通知 |
| `NotificationService` | 类别开关、聚合（60 秒窗口）、排队（专注助手/全屏/暂停）、深链接参数 |
| `DeepLinkService` | 解析通知与命令行参数并导航 |
| `WindowStateService` | 窗口几何保存与恢复、多显示器校正 |
| `ServerProfileService` | 多服务器配置的存储与切换 |
| `WebViewHostService` | 受限 WebView2 的创建、导航拦截、消息通道 |
| `DesktopSettingsService` | 本地设置文件读写 |

### 7.3 受限 WebView2

按 `desktop-specification.md` 第 9 节与 `security-model.md` THR-090：

* 仅加载本地打包资源；
* `NavigationStarting` 拦截一切外部导航；
* 禁用新窗口与开发者工具（Release）；
* 不注入通用宿主对象；仅暴露白名单消息通道（8 条，见 `desktop-specification.md` 9 节表格）；
* WebView2 内不发起网络请求。

终端与编辑器复用 Web 侧的 xterm.js 与 Monaco 集成代码，打包为本地资源。

### 7.4 独立终端窗口

独立 `Window`，标题为「实例名 — 节点名」，位置记忆，最多 8 个，主窗口退出时一并关闭。

## 8. 数据模型

无服务端变更。本地设置模型见 `desktop-specification.md` 第 12 节。

## 9. API 或事件

消费既有接口。事件订阅由 `Servitium.Client` 管理。

## 10. 实施顺序

1. 单实例与应用生命周期；
2. 主题、Mica、自定义标题栏、窗口几何；
3. `NavigationView` 外壳与返回栈（阶段 20 已有骨架，本阶段完善）；
4. 会话服务：本机换票、续期、回退登录、多服务器；
5. **认证组**页面：PG-001、PG-002、PG-003、PG-032；
6. **节点组**：PG-020、PG-021、PG-022；
7. **实例列表组**：PG-010、PG-011、PG-019；
8. 受限 WebView2 宿主与消息通道；
9. 终端集成 + **控制台组**：PG-012、PG-018；
10. 独立终端窗口；
11. 编辑器集成；
12. **文件组**：PG-013（含拖放上传、分片上传）；
13. **配置组**：PG-014、PG-015；
14. **模组组**：PG-016；
15. **计划任务组**：PG-017；
16. **治理组**：PG-030、PG-031、PG-042、PG-043、PG-044；
17. **仪表盘与设置组**：PG-040、PG-041；
18. **分发组**：PG-060、PG-061、PG-062、PG-063；
19. **其他**：PG-050、PG-051；
20. 托盘服务：图标状态、菜单、实例快捷操作、暂停通知；
21. 通知服务：类别、聚合、排队、深链接；
22. 关闭到托盘与退出语义；
23. 本地设置与日志；
24. 响应式（窗口宽度断点）走查；
25. 无障碍走查；
26. 一致性测试纳入全部页面；
27. UI 自动化测试；
28. 运行 `eng/Validate.ps1`。

## 11. 关键算法

| 算法 | 说明 |
| --- | --- |
| 单实例重定向 | 注册应用实例键；非主实例把激活参数重定向后立即退出；主实例在激活事件中恢复窗口并处理深链接 |
| 本机换票 | 仅当服务器地址解析为本机时尝试；管道请求 → 一次性令牌 → 换会话；到期前 2 分钟续期；失败时回退登录并给出具体原因 |
| 通知聚合 | 按类别维护 60 秒窗口的缓冲；窗口结束或达到阈值时发出一条聚合通知（条目数 + 最近摘要） |
| 通知排队 | 检测暂停状态、专注助手、全屏独占；命中则入队（上限 50，超出丢弃最旧的非危险类）；恢复后一次性聚合呈现 |
| 深链接解析 | 参数格式 `servitium://<页面>/<对象标识>?<附加>`；解析后压入返回栈导航 |
| 窗口几何恢复 | 保存位置、尺寸、最大化状态；恢复时校验矩形与任一显示器有交集，否则居中到主显示器 |
| 尺寸断点 | 窗口客户区宽度映射到与 Web 相同的断点表 |
| 托盘菜单构建 | 从最近使用的实例列表（本地设置）+ 实时状态构建；状态变化后 2 秒内重建 |

## 12. Windows 平台事项

* Windows App SDK 与 WebView2 Runtime 为前置条件；缺失时给出明确指引；
* 应用通知使用 `AppNotificationBuilder`；需要注册通知激活回调；
* 托盘图标使用 Shell 通知区 API（经 Windows App SDK 或直接互操作）；
* Mica 通过 `SystemBackdrop`；不支持时回退纯色；
* DPI 与文本缩放运行时变化需正确响应；
* 会话令牌存 Windows 凭据管理器；
* 单实例使用 `AppInstance.FindOrRegisterForKey` 与 `RedirectActivationToAsync`。

## 13. 安全要求

| 项 | 说明 |
| --- | --- |
| `THR-006` | 本机换票：SID 绑定、可执行路径校验、管道 ACL（服务端已实现，本阶段验证客户端行为） |
| `THR-090` | 受限 WebView2：导航拦截、无宿主对象、无网络请求、无开发者工具 |
| 凭据 | 不写入本地设置文件；使用凭据管理器 |
| 日志 | 不记录任何秘密 |
| 深链接 | 参数校验；不接受任意 URL 导航 |

## 14. 自动化测试

`Servitium.Desktop.Tests`（分类 `Unit`）：

| 编号族 | 内容 |
| --- | --- |
| `DUT-100` | 视图模型的状态映射与格式化 |
| `DUT-110` | 通知聚合规则（窗口、阈值、类别） |
| `DUT-120` | 通知排队规则（暂停、专注助手、全屏、上限） |
| `DUT-130` | 深链接解析（合法与非法参数） |
| `DUT-140` | 托盘菜单构建 |
| `DUT-150` | 窗口几何恢复的显示器交集判定 |
| `DUT-160` | 断点映射 |

## 15. 集成测试

`Servitium.Desktop.UiTests`（分类 `Ui`，WinUI UI Automation），跑在真实 Server + 真实 Agent 上。

必须实现 `desktop-specification.md` 第 14 节的 `DUI-001` … `DUI-012` 全部场景，另加：

| 编号 | 内容 |
| --- | --- |
| `DUI-020` | 全部页面可到达且区块顺序与契约一致（一致性测试） |
| `DUI-021` | 本机换票成功路径与三种失败回退路径 |
| `DUI-022` | 终端：输出、输入、快捷命令、独立窗口 |
| `DUI-023` | 编辑器：打开、修改、保存、未保存确认 |
| `DUI-024` | 文件：拖放上传、分片上传中断续传 |
| `DUI-025` | 破坏性操作的确认对话框（含键名确认） |
| `DUI-026` | 长操作进度与取消 |
| `DUI-027` | 多服务器切换 |
| `DUI-028` | 语言切换后全部可见文案变化 |
| `DUI-029` | 主题跟随系统与强制指定 |
| `DUI-030` | DPI 变化后布局正确 |

安全测试：`SEC-360`、`SEC-361`。

## 16. 人工测试

托盘、系统通知、专注助手交互、全屏独占等场景中，部分行为难以完全自动化。这些项在阶段 24 由用户验证；本阶段尽可能自动化，并在 `docs/records/problem-log.md` 中记录无法自动化的项与原因。

## 17. 调试启动方式

```powershell
dotnet run --project src/Servitium.Server -- --environment Development
dotnet run --project src/Servitium.Agent -- --environment Development
dotnet run --project src/Servitium.Desktop
```

WebView2 内容调试：Debug 构建下允许开发者工具。

## 18. 验收责任方

编码 Agent 自主验收；无法自动化的托盘与通知行为在阶段 24 由用户确认。

## 19. 完成条件

1. 全部页面已实现且与契约一致（一致性测试通过）；
2. 单实例、关闭到托盘、退出语义正确；
3. 本机换票与三种回退路径正确；
4. 托盘图标状态、菜单、实例快捷操作正确；
5. 通知的类别、聚合、排队、深链接正确；
6. 受限 WebView2 的导航拦截与消息通道正确；
7. 独立终端窗口可用；
8. 拖放上传可用；
9. `DUI-001` … `DUI-030` 全部通过；
10. `DUT-100` … `DUT-160` 全部通过；
11. `SEC-360`、`SEC-361` 通过；
12. 冷启动到可交互 < 3 秒；页面切换 < 200 毫秒；
13. 无障碍走查通过；
14. `eng/Validate.ps1` 全量通过。

## 20. 禁止提前进入下一阶段的条件

* 存在未实现的页面；
* 一致性测试存在差异；
* WebView2 可导航到外部 URL；
* 退出 Desktop 会影响 Server、Agent 或实例；
* 会话令牌被写入本地设置文件；
* UI 测试存在随机失败。

## 21. 需要更新的动态记录

* `docs/records/current-status.md`
* `docs/records/development-log.md`
* `docs/records/decision-log.md`
* `docs/records/problem-log.md`（含无法自动化的项）
* `docs/records/validation-log.md`
* `docs/testing/requirements-traceability.md`
* `docs/testing/acceptance-matrix.md`
