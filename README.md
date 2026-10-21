# Servitium

面向自有 Windows 主机的 Minecraft Java 服务端托管与 HMCL 整合内容分发平台。当前为开发源码，没有正式安装包；Desktop 首轮重写正在验证，Web 暂停，不能将当前分支视为已验收成品。

## 支持的平台

Desktop：Windows 11 x64。Server 与 Agent：Windows 11、Windows Server 2022/2025 x64。第二 Windows 平台与真实场景尚未全部验证。

## 快速开始

先按[开发环境](documents/development/setup-development.md)准备 global.json 指定的 SDK、Node、Java 和 Windows 组件，再从仓库根目录执行：

```powershell
Copy-Item tools/build/Local.props.example tools/build/Local.props
# 编辑 Local.props，填入本机 dotnet 命令和仓库外的工作区 Artifacts/Servitium 目录。
./tools/build/Build.ps1
./tools/test/Test.ps1
```

依赖安全修复已解除还原阻塞；桌面首轮重写进行中，详见[进度](documents/project/status-development.md)。运行服务与客户端见[工作流](documents/development/workflow-development.md)。

## 文档

| 入口 | 内容 |
| --- | --- |
| [项目规范](documents/AGENTS.md) | 核心路径、事实来源与产品补充约束 |
| [桌面使用](documents/usage/guide-desktop.md) | 连接、实例工作区、设置与验收 |
| [当前进度](documents/project/status-development.md) | 当前状态、开放项、下三步 |
| [产品范围](documents/project/overview-servitium.md) | 面向谁、做什么与不做什么 |
| [系统架构](documents/design/architecture-system.md) | 部署、数据权威与通信 |
| [需求与协议](documents/reference/api-functional_requirements.md) | 功能验收与精确定义 |
| [技术债](documents/project/debt-rules.md) | 存量规范差距，按影响排序 |
| [测试](documents/development/testing-development.md) | 现有测试与真实场景定义 |

## 许可

尚未确定对外许可，当前没有 LICENSE；不授予复制、分发或再许可权利。
