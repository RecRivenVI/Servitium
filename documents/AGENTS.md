# 项目规范

本文件对整个仓库生效，补充[仓库规范](../AGENTS.md)。当前状态以[进度页](project/status-development.md)为准。

## 项目信息

| 项 | 值 |
| --- | --- |
| 产品显示名 | [Directory.Build.props](../Directory.Build.props) 的 Product |
| 一句话 | 面向自有 Windows 主机的 Minecraft Java 服务端托管与 HMCL 整合内容分发平台 |
| 核心路径 | 登记节点与 Java → 创建或附加实例 → 启动、终端交互和文件维护 → 停止；分发路径为检查内容 → 不可变 Release → 显式发布 Channel → HMCL 更新 |
| 技术栈附则 | .NET 与 Web；Web 尚未开始 |
| 支持的平台 | Desktop 为 Windows 11 x64；Server/Agent 为 Windows 11、Windows Server 2022/2025 x64；Web 目标为主流浏览器 |
| 标识 | .NET 项目/根命名空间由各 csproj 的项目名定义；协议稳定名称见[协议](reference/protocol-control_plane.md) |
| 设计语言与组件库 | Windows 为 WinUI 3/Fluent；Web 采用 Fluent UI React v9，不改为 Blazor |
| 测试框架 | xUnit，全项目统一 |
| 文档主语言 | 简体中文 |
| 首页语言 | 简体中文 |
| 上游 | 无；MCSManager、HMCL、Prism 只作为行为或格式参考，不是继承源码的上游 |
| 常用测试环境 | Windows 交互桌面、隔离数据库/目录、合成 Java 服务端；第二 Windows 平台与真实样本尚待验证 |

## 技术栈

| 组成 | 版本与唯一来源 |
| --- | --- |
| .NET SDK | [global.json](../global.json)，不在脚本另定版本 |
| 产品版本与公共编译设置 | [Directory.Build.props](../Directory.Build.props) |
| NuGet、Windows App SDK、MVVM、xUnit | [Directory.Packages.props](../Directory.Packages.props) 与各锁文件；本轮不升级 Windows App SDK |
| Desktop 本地终端/编辑器依赖 | [package.json](../src/Servitium.Desktop/WebAssetsSource/package.json) 与锁文件；这些资源不是 Web 管理端 |
| 本机 SDK、产物根与参考仓库位置 | 被忽略的 tools/build/Local.props；字段定义见[本机配置](reference/configuration-development.md) |
| 命令 | [开发工作流](development/workflow-development.md)和 tools 下的实际入口 |

## 补充规则

- **X-01** 只托管 Minecraft Java 服务端。[产品范围](project/overview-servitium.md)及[用户固定决策](design/adr-1-product_baseline.md)定义产品能力；不借整理新增通用进程、Steam、基岩版、Docker、WebShell、市场、穿透、商业功能、旧数据兼容或 Portable。
- **X-02** 净室实现：可以观察外部行为、格式、协议和标识，不复制、移植或改写复用参考项目的源码、资源、样式、图标、文案或品牌。参考来源见[来源说明](reference/upstream-references.md)。参考仓库只读；探针/编译/依赖与结果放在参考路径之外。
- **X-03** 真实服务器、ZIP、Prism 包、HMCL 安装、Release、备份和日志属于用户数据。没有具体授权不读取、复制、解包、登记、转换或运行；不以用户真实目录为测试输入。参考位置先按工作区管理规则定位，再写本机配置，不沿用旧绝对路径。
- **X-04** 实例 ID 与目录分离。外部工作目录永不递归删除；删除登记不触碰文件。只有明确 OwnedByServitium 的受管目录可经二次校验执行已确认的目录动作。路径、归档与出站下载的安全验证不能因 C-02 而省略，它们针对可达的不受信输入。
- **X-05** 实例无 shell 启动，Java 参数数组与已登记运行时绑定；进程树由独立 Job Object 约束。秘密不得进入日志、事件、错误、诊断或公开响应；本地运行账户密码不经控制平面传输。安全不变量见[安全模型](design/security-threat_model.md)。
- **X-06** Server 为授权与通信入口，Desktop/Web 不直连 Agent；Agent 主动连接并独立校验命令/路径。Server 停止不等于 Agent 崩溃：前者保持本地自治，后者依进程树策略回收。Desktop 退出不停止服务或实例。
- **X-07** 保留 HMCL manifest 的 no-cache，以及 overrides 的恒不压缩、SHA-1 强 ETag/Range 等已证实兼容条件。参考新证据不能自行改变产品范围或协议；差异写入 debt、backlog 或研究记录，再由用户决定。
- **X-08** 用户已允许先完成 Desktop、暂停 Web 与四项真实样本测试。该例外只影响先后，不表示用户验收完成；本轮界面由外部评审另行设计，不改界面、不升级 Windows App SDK。旧 Gate/Profile 已退役，不保留为新的流程要求。
- **X-09** 真实服务、ACL、关机、多主机、第二 Windows、真实 Minecraft/HMCL 与人工视觉验证使用 validations 中定义和独立外部目录；缺少环境如实写无法比较，不用历史结果或静态审查宣称当前完成。
- **X-10** 交付为独立用户决定。没有明确验收与后续交付授权，不创建安装包、标签、发布或远程部署；本轮只授权一个 Update 检查点并正常推送 develop，legacy 不动。

存量代码与规范不符的内容见[技术债](project/debt-rules.md)，不以补充规则放宽。确认过的产品和设计依据保留正文；其中退役的治理引用不再约束当前工作流。
