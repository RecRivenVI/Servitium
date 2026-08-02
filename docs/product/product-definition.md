# Servitium 产品定义

## 1. 产品陈述

Servitium 是一套面向 Windows 的 Minecraft Java 服务端托管与整合内容分发平台。它让管理员在自有的一台或多台 Windows 主机上创建、运行、监控和维护 Minecraft Java 服务端实例，并把服务端整合内容以 HMCL 客户端可自动更新的形式分发给玩家。

Servitium 是可长期维护、可正式交付的完整产品。

## 2. 组成

| 组件 | 形态 | 职责 |
| --- | --- | --- |
| `Servitium.Server` | Windows 服务，ASP.NET Core | 中央控制平面。用户与会话、节点登记、全局授权、审计、系统设置、Web 静态资源托管、HMCL 分发端点、REST API 与实时事件 |
| `Servitium.Agent` | Windows 服务 | 节点执行体。Java 运行时、实例生命周期、进程与终端、文件、配置、模组、本地计划任务、本地指标 |
| `Servitium.Desktop` | WinUI 3 桌面应用 | Windows 11 原生客户端。导航、托盘、系统通知、本机免登 |
| `Servitium.Web` | React SPA | 浏览器管理界面，由 `Servitium.Server` 托管构建产物 |
| HMCL 分发 | `Servitium.Server` 内的能力 | 归档、Release、Channel、`server-manifest.json` 与 `overrides/` 内容服务 |

## 3. 目标用户

| 用户 | 关键需求 |
| --- | --- |
| 在自有 Windows 主机上运行 Minecraft Java 服务端的个人管理员 | 一台机器上装完即用；启动、停止、看控制台、改配置、传文件 |
| 管理多个 Windows 节点与多个实例的小型团队 | 跨节点统一视图、按用户授权到实例、操作可追溯 |
| 需要通过浏览器远程管理服务端的用户 | 不装客户端也能完成全部日常管理；远程访问有明确的安全边界 |
| 需要统一向玩家分发服务端整合内容的服务器运营者 | 一次发布，玩家的 HMCL 客户端自动同步；可回滚；发布前能看到影响面 |

## 4. 产品原则

1. **Windows 优先且只做 Windows。** ConPTY、Job Object、注册表探测、MSI、WinUI 3、NTFS 语义都是一等公民，不为跨平台做抽象妥协。
2. **只托管 Minecraft Java 服务端。** 不做通用进程托管，因此可以在启动配置、配置文件识别、模组解析、玩家查询上给出具体而非泛化的能力。
3. **自托管闭环。** 不依赖任何厂商在线服务。允许并鼓励的网络能力是：自托管 Web、控制平面与节点通信、HMCL 分发、URL 下载、私有源、可自托管 OIDC。
4. **默认安全。** 默认仅监听回环；非回环暴露必须显式配置受信 TLS 或可信反向代理；所有破坏性操作需二次确认；外部工作目录永不递归删除。
5. **两端一致。** Desktop 与 Web 的信息架构、字段、动作、状态、权限与文案一致；只允许平台相关的响应式重排与平台原生能力差异。
6. **控制平面不是单点。** 控制平面停止时，节点上的实例继续运行，本地计划任务继续执行；恢复后自动对账。
7. **可追溯。** 谁做了什么、结果如何、安全相关事件发生了什么，都能查到。

## 5. 范围边界

### 5.1 包含

身份与安全、节点管理、Java 运行时、Minecraft Java 实例、终端与进程、文件管理、配置管理、模组与插件、计划任务、用户与仪表盘与治理、Desktop 专项能力、HMCL 分发。逐条能力见 `functional-specification.md`。

### 5.2 排除

| 排除项 | 理由 |
| --- | --- |
| 通用进程托管 | 与「只做 Minecraft Java」冲突，会稀释所有针对性能力 |
| Steam / SteamCMD | 同上 |
| Minecraft 基岩版 | 协议、配置、模组生态完全不同 |
| MCDReforged | 引入第三方运行时依赖与其自身的插件生态 |
| Docker 与镜像管理 | Windows 上的 Docker 对 Minecraft 服务端托管收益低、复杂度高 |
| 全局 WebShell | 提供了绕过全部实例级授权的通道 |
| 模板市场 / 快速安装市场 | 依赖厂商在线目录 |
| 商业兑换与商城 | 与产品目标无关 |
| 内置内网穿透（如 OpenFRP） | 依赖厂商服务 |
| 第三方模组市场检索与下载 | 依赖厂商在线目录 |
| 旧产品数据迁移 / 旧架构兼容层 | `develop` 是不继承旧产品代码的全新起点 |
| Portable 运行模式 | 与「以 Windows 服务常驻」的运行形态冲突 |
| 其他启动器的整合包格式 | 只支持 HMCL 的服务端整合内容协议 |

## 6. 运行形态

* `Servitium.Server` 与 `Servitium.Agent` 均以 Windows 服务常驻，随系统启动，配置服务恢复策略。
* 单机场景下，Server 与 Agent 可安装在同一台机器上，Agent 仍通过本机回环连接 Server。
* `Servitium.Desktop` 是普通用户态应用，退出 Desktop 不影响 Server、Agent 或任何实例。
* Web 界面由 `Servitium.Server` 直接托管，无需额外 Web 服务器。

## 7. 系统支持矩阵

| 组件 | 支持系统 |
| --- | --- |
| `Servitium.Desktop` | Windows 11 x64 |
| `Servitium.Server` | Windows 11 x64、Windows Server 2022 x64、Windows Server 2025 x64 |
| `Servitium.Agent` | Windows 11 x64、Windows Server 2022 x64、Windows Server 2025 x64 |
| `Servitium.Web` | 最近两个稳定版本的 Edge、Chrome、Firefox |

## 8. 容量目标

Servitium 在下列规模下必须满足功能与性能验收：

| 维度 | 目标 |
| --- | --- |
| Agent 节点数 | 8 |
| Minecraft Java 实例总数 | 64 |
| 并发登录会话数 | 20 |
| 并发终端会话数 | 8 |

超出该目标不构成缺陷，但必须优雅降级而非崩溃。

## 9. 交付形态

* **完整安装包**：包含 `Servitium.Server`、`Servitium.Agent`、`Servitium.Desktop` 与 Web 构建产物。
* **远程 Agent 专用安装包**：仅包含 `Servitium.Agent`。
* 不提供 Portable 形态。

交付是用户验收之后的独立任务，必须由用户明确启动，流程见 `docs/delivery/delivery-plan.md`。

## 10. 首发语言

`zh-Hans` 与 `en-US`。规则见 `localization-specification.md`。

## 11. 界面基线

Fluent 2 设计体系。Desktop 使用 WinUI 3 与 Windows App SDK；Web 使用 Fluent UI React v9。MCSManager 仅提供功能、信息架构与交互行为参考，其控件、颜色、图标、样式、资源与文案一律不复用。

## 12. 相关文档

* `decision-baseline.md`：不可协商的决策清单
* `functional-specification.md`：逐条功能需求与验收
* `hmcl-distribution-specification.md`：HMCL 分发产品规格
* `localization-specification.md`：本地化规则
* `docs/architecture/system-architecture.md`：系统架构
* `docs/design/information-architecture.md`：信息架构与页面清单
