# Servitium

面向 Windows 的 Minecraft Java 服务端托管与整合内容分发平台。

## 这是什么

Servitium 让你在自有的一台或多台 Windows 主机上创建、运行、监控和维护 Minecraft Java 服务端实例，并把服务端整合内容以 HMCL 客户端可自动更新的形式分发给玩家。

产品由五部分组成：

| 组件 | 说明 |
| --- | --- |
| `Servitium.Server` | 中央控制平面与 Web 服务，以 Windows 服务常驻 |
| `Servitium.Agent` | 部署在本机或远程 Windows 节点上的执行服务，以 Windows 服务常驻 |
| `Servitium.Desktop` | Windows 11 上的 WinUI 3 桌面客户端 |
| `Servitium.Web` | 通过浏览器访问的 React 管理面板 |
| 整合内容分发 | 面向 HMCL 客户端的自动更新与发布能力 |

## 目标平台

| 组件 | 支持系统 |
| --- | --- |
| `Servitium.Desktop` | Windows 11 x64 |
| `Servitium.Server`、`Servitium.Agent` | Windows 11 x64、Windows Server 2022 x64、Windows Server 2025 x64 |
| `Servitium.Web` | 最近两个稳定版本的 Edge、Chrome、Firefox |

界面语言：简体中文与英文。

## 能做什么

* 在多个 Windows 节点上统一管理 Minecraft Java 服务端实例；
* 实时控制台、进程树可控的启动与停止、崩溃识别与自动重启；
* 文件管理、配置文件的无损结构化编辑、模组与插件管理；
* 计划任务在节点本地执行，控制平面离线时照常运行；
* 按用户逐实例授权，操作可追溯，安全事件独立审计；
* 一次发布整合内容，玩家的 HMCL 客户端在下次启动时自动同步；
* 浏览器与桌面客户端提供一致的信息架构、字段、动作与文案。

## 不做什么

通用进程托管、Steam / SteamCMD、Minecraft 基岩版、MCDReforged、Docker 与镜像管理、全局 WebShell、模板与快速安装市场、商业兑换与商城、内置内网穿透、第三方模组市场检索与下载、Portable 运行模式、其他启动器的整合包格式。

## 开发状态

**本仓库处于开发中，尚未产生可用于生产环境的版本。**

当前没有可下载的安装包或 Release。请不要把本仓库的任何内容用于承载真实玩家数据的服务器。

## 文档

完整文档在 [`docs/`](docs/index.md)。

| 入口 | 说明 |
| --- | --- |
| [文档索引](docs/index.md) | 全部文档的地图与阅读路径 |
| [产品定义](docs/product/product-definition.md) | 产品陈述、范围与边界 |
| [功能规格](docs/product/functional-specification.md) | 逐条需求与验收 |
| [系统架构](docs/architecture/system-architecture.md) | 拓扑、分层与技术选型 |
| [安全模型](docs/architecture/security-model.md) | 威胁与应对 |
| [开发计划](docs/development/master-plan.md) | 阶段顺序与门禁 |
| [AGENTS.md](AGENTS.md) | 参与开发的 Agent 必须遵守的规则 |

## 许可

本仓库尚未确定对外许可。在许可文件加入仓库前，不授予复制、分发或再许可权利。

## 参与开发

开始编码前请先阅读 [AGENTS.md](AGENTS.md) 与 [开发工作流](docs/development/development-workflow.md)。
