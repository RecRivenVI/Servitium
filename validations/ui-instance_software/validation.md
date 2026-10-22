# 实例软件信息检查点

## 目标

核对 77 号第一个检查点：从明确启动元数据识别软件，Server/Client 回读与 Desktop 列表、标题、检查栏一致；未知和离线快照有原因。仅用自有隔离环境和合成文件，不执行真实服务器或读取用户样本。

## 运行

1. 按[工作流](../../documents/development/workflow-development.md)构建受影响项目，运行 InstanceSoftwareInspectorTests、AgentCommandDispatcherTests、ServitiumApiClientTests、CoreEndpointWorkflowTests、InstanceConsoleViewModelTests 与 InstanceWorkspaceViewModelTests；不跑完整矩阵。
2. 在仓库外自有 Server/Agent 中登记停止状态的合成实例，分别写入原版 JAR、Fabric 主清单/依赖坐标、NeoForge 参数文件。用当前 Client 经真实控制平面回读，对照文件明确值；普通账户不得看到无 grant 的夹具。
3. 在同一自有目录替换元数据，核对重新对账后清除旧值。缺失、损坏、冲突、高级参数、超限和路径边界由格式夹具验证，不为了截图构造真实系统故障。
4. 桌面使用隔离数据目录及不激活启动，提前准备所选合成实例与应用主题；按 HWND 截取宽窄、深浅、无法读取及节点离线画面并亲看。不发送键鼠，不设置焦点，不执行本机已知会激活窗口的 UIA 选择操作。
5. 读取 UIA 树检查既有入口的可聚焦属性；实际选择、刷新与真实键盘手感交[人工清单](../../documents/usage/guide-desktop_acceptance.md)。退出自己的测试进程，恢复合成元数据，将临时备份送回收站。

## 通过标准

- 一致：明确格式的版本与加载器通过实际 Agent/Server/Client 链路回读，Desktop 三处同源；替换文件后不会保留旧版本，未知不从名称猜测。
- 一致：缺少软件字段的旧响应可读；未授权实例拒绝详情且不进入列表/分组，既有授权、CSRF 与路径边界不变。
- 画面在指定尺寸和主题下可读，未知原因与离线读取时间真实；运行结果及截图只放仓库外。
- 无法比较的真实安装样本、最大系统字号、读屏与交互手感明确列出；不能用合成文件或截图替代用户验收。
