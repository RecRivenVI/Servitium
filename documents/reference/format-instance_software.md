# 实例软件识别

实例的软件信息来自 Agent 对已登记启动目标的只读观察，不依据实例名、JAR 文件名或玩家查询返回的显示名称推测。列表、标题和控制台检查栏共用 `InstanceSoftwareDto`；未知字段为 `null`，并携带原因和读取时间。

## 识别依据

| 格式 | 明确读取的内容 | 边界 |
| --- | --- | --- |
| Minecraft JAR | 主清单的 Minecraft 服务端/捆绑器主类，及根 `version.json` 的唯一字符串 `id` | 缺少版本时只报告原版类型；其他主类即使有 `version.json` 也不认作 Minecraft |
| Fabric 安装器启动 JAR | 已知 Fabric 服务端主类，清单续行展开后的 `Class-Path` 中 `net/fabricmc/intermediary` 与 `fabric-loader` 的完整 Maven 坐标 | 版本取坐标内容，相关依赖须为工作目录内存在的普通文件；多个不同版本作为冲突，不按目录名或 JAR 名猜测 |
| Forge/NeoForge 参数文件 | 已知服务端主类、对应的启动目标及独立行 `--fml.mcVersion`、`--fml.forgeVersion` 或 `--fml.neoForgeVersion` | 只识别这种明确布局；未物化的占位符、混在其他行的选项、重复版本字段不产生版本值 |

格式参照：[Fabric 安装器](https://github.com/FabricMC/fabric-installer/blob/master/src/main/java/net/fabricmc/installer/server/ServerInstaller.java)、[NeoForge 参数文件](https://github.com/neoforged/NeoForge/blob/1.21.x/server_files/args.txt)、[Forge 参数文件](https://github.com/MinecraftForge/MinecraftForge/blob/1.20.x/server_files/args.txt)。只观察格式，不复用源码。当前 Forge 的 shim 启动布局未提供上述版本信息，因此保持未知，不误套 FML 格式。

参考项目许可分别为 [Fabric 的 Apache-2.0](https://github.com/FabricMC/fabric-installer/blob/master/LICENSE)、[NeoForge 的 LGPL-2.1](https://github.com/neoforged/NeoForge/blob/1.21.x/LICENSE.txt)及 [Forge 的 LGPL-2.1 与例外说明](https://github.com/MinecraftForge/MinecraftForge/blob/1.20.x/LICENSE.txt)；本项目没有引入这些源码或发行包。

## 读取与刷新

创建、修改配置和节点对账时重新读取；控制台先请求真实运行状态触发对账，再读取实例详情。工作区“刷新”会重新加载控制台，将同一结果同步到列表与标题；未发送命令保留。断线时只展示最后一次读取的快照，检查栏明确标注读取时间与重连后刷新。

沿用既有实例查看授权、Agent 路径边界和 JSON 镜像持久化，没有新增授权入口或数据表。`AgentInstanceResponse.Software`、`InstanceDto.Software` 均为可选新增字段；旧响应或旧镜像缺失字段时保持未知。OpenAPI 从当前契约重新导出，gRPC 指令仍复用既有 `Reconcile` JSON 载荷。

## 无法识别

- 高级启动数组不保证采用已登记目标，返回 `advanced-launch`。
- 自定义包装器、Paper/Quilt、Fabric 新式自下载包装器、Forge shim、嵌套参数文件等未覆盖布局，返回 `unrecognized-launch-format`；不执行或下载它们来推断版本。
- 启动文件缺失、依赖缺失、冲突、越界/链接、不可读或损坏，各有独立原因。元数据单项最多读取 64 KiB，超限作为不可读；只打开指定条目，不解包或运行 JAR。
- 这些字段说明文件所声明的软件，不证明安装完整、实际运行版本或文件可信。真实服务器样本仍按项目暂停决定未验证。

识别回归使用合成清单、参数文件和 JAR，覆盖换文件后的重新读取、续行、冲突、缺失、非法内容、超限及路径边界；服务链验证对照合成文件明确值与 Server/Client 回读。视觉证据与本次验证结论见[当前进度](../project/status-development.md)。
