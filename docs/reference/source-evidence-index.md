# 参考证据索引

本文件登记外部项目与本地原型的证据快照、取证方法、验证级别和净室边界。`docs/reference/` 中的事实应能追溯到这里登记的 Git 提交或非 Git 文件快照。

## 1. 初始证据快照

### 1.1 MCSManager

| 项目 | 实际值 |
| --- | --- |
| 本地路径 | `D:\GitHubPublic\MCSManager` |
| 远端 | `https://github.com/MCSManager/MCSManager.git` |
| 分支 | `master` |
| HEAD 提交 | `072fc596767d9719ca925085b91c0df56caffa24` |
| `git describe` | `v10.17.0-15-g072fc596` |
| HEAD 提交时间 | 2026-07-27 10:23:40 +0800 |
| HEAD 提交标题 | `Merge pull request #2237 from mliem2k/fix/daemon-config-json-parse-crash` |
| 工作树状态 | 干净，无未提交修改 |
| 取证日期 | 2026-08-02 |

工作区结构：`common/`（共享 TypeScript 库）、`panel/`（Koa 控制平面）、`daemon/`（节点守护进程）、`frontend/`（Vue 3 + Ant Design Vue 前端）、`languages/`（12 个语言包，`en_US.json` 含 3453 条文案键）。

### 1.2 HMCL

| 项目 | 实际值 |
| --- | --- |
| 本地路径 | `D:\GitHubPublic\HMCL` |
| 远端 | `https://github.com/HMCL-dev/HMCL.git` |
| 分支 | `main` |
| HEAD 提交 | `d297417909208deb3a4bb8378af4769f97de44c8` |
| `git describe` | `v3.17.0.351-37-gd29741790` |
| HEAD 提交时间 | 2026-07-28 09:40:06 +0800 |
| 工作树状态 | 干净，无未提交修改 |
| 取证日期 | 2026-08-02 |

模块结构：`HMCLCore/`（协议、任务、下载、整合包）、`HMCL/`（JavaFX 界面与启动流程）、`HMCLBoot/`、`minecraft/`、`buildSrc/`。

### 1.3 Prism Launcher

| 项目 | 实际值 |
| --- | --- |
| 本地路径 | `D:\GitHubRecRivenVI\PrismLauncher` |
| 远端 | `https://github.com/RecRivenVI/PrismLauncher.git` |
| 分支 | `develop` |
| HEAD 提交 | `7174c26a90662fdedf736b7457b13a1a25f81f4e` |
| `git describe` | `11.0.0-283-g7174c26a9` |
| HEAD 提交时间 | 2026-07-28 10:20:30 +0000 |
| HEAD 提交标题 | `Add wildcard support to instance name field (#5837)` |
| 工作树状态 | 干净，无未提交修改 |
| 取证日期 | 2026-08-03 |

取证范围集中在 Prism Launcher 原生“导出实例”ZIP：归档入口、文件选择、实例根、`instance.cfg`、`mmc-pack.json`、游戏目录和导入识别。Modrinth 与 CurseForge 导出只用于确认格式边界，不与原生实例 ZIP 混用。

### 1.4 本地整合包发布原型

| 项目 | 实际值 |
| --- | --- |
| 本地路径 | `E:\Minecraft\MinecraftServersDuoyun\test` |
| 版本控制 | 非 Git 仓库 |
| 证据文件 | 根 README、8 个 PowerShell 脚本、3 个静态页面文件、3 个 JSON 配置结构 |
| 证据文件数与大小 | 15 个，共 121535 字节 |
| 证据文件最晚修改时间 | 2026-07-30T06:33:29Z |
| 证据清单 SHA-256 | `857b6d8c5fffa0644e0c47351dbef2908a2fa97a8ee8c6322caac82fc78c2787` |
| 取证日期 | 2026-08-03 |

该原型展示 Prism 导出 ZIP 到候选记录、不可变 Release、稳定 Channel、动态 HMCL manifest 与动态 Prism `.mrpack` 的本地工作流。真实归档、发布物、日志、运行状态与备份不属于本次证据集，其内容没有被读取。

## 2. 初始取证方法

### 2.1 MCSManager

初始快照采用源码静态取证，覆盖：

* `frontend/src/config/router.ts`：完整前端路由树与权限常量；
* `panel/src/app/service/frontend_layout.ts`：页面到卡片组件的默认布局映射；
* `frontend/src/widgets/**`：全部 76 个页面级与对话框级组件；
* `panel/src/app/routers/**`：全部 17 个 HTTP 路由文件；
* `daemon/src/routers/**`：全部 11 个 Socket.IO 路由文件；
* `panel/src/app/service/**`、`daemon/src/service/**`：权限、审计、文件、计划任务、Java、模组服务；
* `daemon/src/entity/**`：实例配置、守护进程配置、生命周期与状态常量；
* `languages/en_US.json`：文案键解码，用于确定界面实际标签语义。

界面标签通过语言包解码得到，用于理解功能语义；Servitium 不复用任何 MCSManager 文案字符串。

阶段 00 没有运行 MCSManager：当时需要从外部包仓库获取 panel、daemon 与 frontend 三套依赖并启动三个进程，而环境访问不稳定，因此该快照只记录能够由源码定位的路由、页面结构、字段、权限、数据模型与行为链，并统一标注为**源码验证**。`[SOURCE]` 只说明这批证据的取得方式，不表示运行验证没有价值；后续实现涉及可观察行为、交互细节或版本差异时，Agent 应在隔离环境中做针对性运行核实并记录结果。

### 2.2 HMCL

初始快照同时采用源码静态取证与运行验证。

源码取证覆盖：

* `HMCLCore/src/main/java/org/jackhuang/hmcl/modpack/server/`：`ServerModpackManifest`、`ServerModpackProvider`、`ServerModpackCompletionTask`、`ServerModpackRemoteInstallTask`、`ServerModpackLocalInstallTask`、`ServerModpackExportTask`；
* `HMCLCore/.../modpack/`：`ModpackConfiguration`、`ModpackInstallTask`、`MinecraftInstanceTask`、`ModpackUpdateTask`、`ModAdviser`、`Modpack`；
* `HMCLCore/.../task/`：`FetchTask`、`GetTask`、`FileDownloadTask`；
* `HMCLCore/.../util/io/NetworkUtils`、`HMCLCore/.../util/CacheRepository`；
* `HMCLCore/.../download/LibraryAnalyzer`、`GameBuilder`；
* `HMCLCore/.../game/DefaultGameRepository`、`HMCL/.../game/HMCLGameRepository`；
* `HMCL/.../game/LauncherHelper`、`HMCL/.../game/ModpackHelper`；
* `HMCL/.../ui/download/ModpackSelectionPage`、`RemoteModpackPage`、`ModpackInstallWizardProvider`。

运行验证方法：

1. 使用 `javac` 直接编译 `HMCLCore/src/main/java` 全部 430 个源文件到临时目录（依赖 jar 取自本机 Gradle 缓存，通过一次性 init script 导出运行时 classpath；HMCL 仓库未被修改，未新增任何文件）。
2. 在临时目录编写独立探针程序，启动 `com.sun.net.httpserver` 本地 HTTP 服务，提供 `server-manifest.json` 与 `overrides/` 内容，并记录每一次请求的方法、原始路径与请求头。
3. 构造一个已安装的本地实例目录（含旧 `modpack.json`）与一份新的远端 manifest，覆盖新增、修改、删除、用户已改动、本地缺失、不受管理、`.disabled` 兄弟文件、非 ASCII 与含空格路径等场景。
4. 直接驱动 HMCL 自身的 `ServerModpackCompletionTask` 执行，观察真实 HTTP 流量与真实文件系统结果。
5. 分三轮执行，分别观察首轮全量、次轮条件重校验、以及服务端返回 `304` 时的行为，并额外验证 `Cache-Control: max-age` 对客户端的影响。

探针程序与编译产物全部位于会话临时目录，未写入任何仓库；验证结束后不保留。

标注为**运行验证**的结论均来自上述执行；未能通过运行触发的分支（断点续传、`416`、重定向、超时重试）标注为**源码验证**，并在 `hmcl-client-behavior.md` 中逐条注明。

### 2.3 Prism Launcher

采用只读源码取证，覆盖：

* `launcher/ui/MainWindow.cpp`、`launcher/ui/dialogs/ExportInstanceDialog.cpp`：原生实例导出入口、文件选择与输出类型；
* `launcher/archive/ExportToZipTask.cpp`、`launcher/FileIgnoreProxy.cpp`：ZIP 路径、符号链接处理和默认排除项；
* `launcher/minecraft/MinecraftInstance.cpp`、`launcher/minecraft/PackProfile.cpp`：游戏目录选择与 `mmc-pack.json` 结构；
* `launcher/InstanceImportTask.cpp`：Prism/MultiMC 原生包、Modrinth 包与 CurseForge 包的识别边界；
* `launcher/ui/dialogs/ExportPackDialog.cpp`：平台格式导出，用于避免把 `.mrpack` 或 CurseForge ZIP 误认为原生实例 ZIP。

没有构建或运行 Prism Launcher，也没有生成、打开或解析用户实例包。结论统一标注为**源码验证**，格式事实与 Servitium 转换约束见 `prism-instance-export-format.md`。

### 2.4 本地整合包发布原型

采用只读静态取证，覆盖：

* README：候选版本、稳定 Channel、不可变 Release、显式发布与用户操作流程；
* PowerShell AST：脚本入口、函数边界、后台转换与服务路由；
* 配置键结构：候选、包、Channel、Release ID 与过滤配置的职责划分；
* 静态目录页：HMCL 与 Prism 链接的公开呈现；
* 关键调用链：Prism ZIP 结构识别、路径过滤、哈希索引、动态 manifest、动态 `.mrpack` 与不可变 Release 下载。

没有启动原型、执行转换、打开真实 ZIP、读取 Release 文件、解析日志或使用历史备份。结论统一标注为**源码验证**，完整边界与事实见 `local-pack-publishing-prototype.md`。

## 3. 净室边界

初始取证和后续实地考察均须遵守净室原则。登记的 Git 仓库与本地原型路径都只能作为只读证据来源。

允许的行为：

* 阅读公开源码以理解外部可观察行为；
* 记录文件路径、类型名、方法名与调用关系；
* 记录路由、协议字段、状态机与用户流程；
* 在用户明确授权且隔离完成时记录真实网络请求与运行结果；
* 基于事实为 Servitium 重新设计。

禁止的行为：

* 复制或翻译后复制任何源码；
* 复制资源文件、样式表、图标、界面文案或品牌元素；
* 将参考项目代码迁入 Servitium；
* 在文档中粘贴大段参考源码；
* 把本地原型中的真实包名、归档内容、Release 内容、日志、备份、过滤清单或其他用户数据带入 Servitium。

本文档族中出现的参考项目标识符（类名、方法名、路由路径、配置字段名）仅用于定位证据，不构成对其实现的复制。Servitium 的 REST 资源命名、gRPC 服务定义、数据库结构、界面控件与全部文案均为独立设计，见 `docs/architecture/` 与 `docs/design/`。

## 4. 证据引用格式

`docs/reference/` 中的证据条目统一使用下列格式：

```text
MCSM@072fc59 : panel/src/app/routers/instance_admin_router.ts :: router.post("/")
HMCL@d297417 : HMCLCore/.../modpack/server/ServerModpackCompletionTask.java :: execute()
PRISM@7174c26a9 : launcher/ui/dialogs/ExportInstanceDialog.cpp :: doExport()
PACKPROTO@857b6d8c : scripts/Convert-PrismPack.ps1 :: source snapshot
```

前三个标识指向 Git 提交，`PACKPROTO` 指向证据清单 SHA-256 的前缀。运行验证结论额外标注 `[RUNTIME]`，源码或静态文件检查结论标注 `[SOURCE]`。

## 5. 后续实地考察与证据更新

初始证据只对第 1 节登记的提交或文件快照负责，不声明参考项目在其他版本上的行为，也不阻止后续核实。编码 Agent 在实现或测试需要时，应直接前往以下绝对路径：

```text
D:\GitHubPublic\HMCL
D:\GitHubPublic\MCSManager
D:\GitHubRecRivenVI\PrismLauncher
E:\Minecraft\MinecraftServersDuoyun\test
```

考察前记录 Git 仓库的当前分支、提交和工作树状态，或记录非 Git 参考的证据文件时间与清单哈希。围绕当前问题定位源码与外部可观察行为；需要运行验证时，在参考路径外使用临时目录或隔离副本。四个参考路径本身保持只读，不得安装依赖、生成构建产物或留下修改。本地原型的用户数据边界按 `local-pack-publishing-prototype.md` 执行。

新证据应带有仓库、提交、路径或运行命令、观察结果与 `[SOURCE]` / `[RUNTIME]` 级别。证据确认既有决定时记入动态记录；证据与本文档族或其他受保护文档冲突，或者需要改变产品、协议、界面、阶段或安全决定时，按 `AGENTS.md` 在 `docs/records/change-requests.md` 登记并等待用户裁决。不得删除、篡改或重新解释旧证据来迎合当前实现。

## 6. 相关文档

* `docs/reference/mcsmanager-feature-inventory.md`
* `docs/reference/mcsmanager-behavior-matrix.md`
* `docs/reference/mcsmanager-frontend-layout.md`
* `docs/reference/hmcl-auto-update-protocol.md`
* `docs/reference/hmcl-client-behavior.md`
* `docs/reference/prism-instance-export-format.md`
* `docs/reference/local-pack-publishing-prototype.md`
