# 本地整合包发布原型

调查日期：2026-08-02；Prism 和本地原型快照为 2026-08-03。

## 问题

记录该快照中的可观察行为，作为独立实现的依据。

## 方法

下述 SOURCE/RUNTIME 等级及提交只代表原调查，不代表当前参考仓库或本产品验收。原始证据外置保存。

## 发现

证据快照：`PACKPROTO@857b6d8c`，验证方式为 `[SOURCE]`。本文档记录本地整合包发布原型的转换、不可变 Release、稳定 Channel 和显式发布流程。

## 1. 证据范围

该路径不是 Git 仓库，证据以 2026-08-03 的只读文件快照定位：

| 项 | 值 |
| --- | --- |
| 绝对路径 | `本机配置 ReferencesPackPrototype` |
| 项目形态 | PowerShell 本地发布原型，含 WPF 管理器、`HttpListener` 分发服务与静态目录页 |
| 证据文件 | 根 `README.md`、`scripts/` 8 个脚本、`public/` 3 个静态文件、`config/` 3 个 JSON 结构，共 15 个文件 |
| 证据总大小 | 121535 字节 |
| 证据文件最晚修改时间 | 2026-07-30T06:33:29Z |
| 证据清单 SHA-256 | `857b6d8c5fffa0644e0c47351dbef2908a2fa97a8ee8c6322caac82fc78c2787` |
| 取证方式 | README 与脚本只读检查、PowerShell AST 函数清单、JSON 键结构检查、静态页面行为检查 |

证据清单先把相对路径分隔符统一为 `/`，再按路径不区分大小写排序。每行采用 `相对路径<TAB>字节数<TAB>文件 SHA-256<LF>`，全部行以 UTF-8 无 BOM 编码后计算清单 SHA-256。该摘要只用于识别登记的证据文件，不代表整个目录的内容哈希。

下列目录包含真实归档、不可变发布物、日志、运行状态或历史备份，不属于登记的证据集：

```text
inbox/*.zip
storage/releases/**
storage/prism-indexes/**
storage/runtime/**
logs/**
.staging/**
```

## 2. 原型职责与数据流

原型把准备发布和正式发布分成两个独立动作：

```text
Prism 导出 ZIP
  → 候选记录
  → 只读检查或转换
  → 内容寻址的不可变 Release
  → 人工把候选 Release 发布到稳定 Channel
  → 按 Channel 动态生成 HMCL manifest 或 Prism 更新包
```

候选记录保存源 ZIP、显示版本、作者、描述、启用状态与最近一次转换得到的 Release ID。转换只更新候选记录，不改变任何稳定链接。Channel 保存一次发布时的 Release ID 与元数据快照；只有显式发布动作才切换稳定 Channel。

这种分离避免“转换完成即影响玩家”：同一个候选可以反复检查和转换，稳定 URL 仍指向上一次明确发布的 Release。

## 3. Prism 导出包识别与转换

原型对 Prism 导出 ZIP 进行下列结构识别。Prism Launcher 自身的格式证据见 `../reference/format-prism_export.md`。

1. 归档中必须能定位唯一的 `instance.cfg`；
2. 同一实例根下必须存在 `mmc-pack.json`；
3. 游戏目录接受 `minecraft/` 或 `.minecraft/`；
4. 从 `mmc-pack.json` 的组件列表提取 Minecraft 与加载器版本；
5. 未映射且不能安全忽略的重要组件会阻断转换，而不是静默丢失；
6. 游戏目录中的文件经过路径检查与过滤后进入 Release；启动器状态、本地缓存和用户状态由过滤规则排除。

转换支持只生成计划而不写发布物。计划结果包含游戏目录前缀、选中与排除文件数、选中文件总大小以及解析出的加载器信息，适合在正式写入前呈现预览。

Release 身份由排序后的相对路径、文件 SHA-1 与加载器声明共同确定，并使用 SHA-256 形成内容地址。Release 目录只包含索引与 `overrides/` 内容；已存在的同一 Release 被复用，不因候选记录变化而修改。

## 4. Channel、HMCL 与 Prism 输出

稳定 Channel 使用包路径与可选频道路径组成长期 URL。原型按请求动态生成：

| 输出 | 行为 |
| --- | --- |
| `server-manifest.json` | 读取 Channel 当前 Release，组合名称、作者、版本、描述、`fileApi`、文件与加载器声明 |
| Channel `overrides/` | 从 Channel 当前 Release 提供文件，适合 HMCL 使用稳定 Channel URL |
| `prism-pack.mrpack` | 动态创建只含 `modrinth.index.json` 的轻量包，不重复打包 Release 内容 |
| 不可变 Release `overrides/` | Prism 索引中的下载地址固定指向具体 Release ID，避免更新途中 Channel 切换造成混包 |
| `/api/catalog` | 向静态目录页提供包、Channel、发布状态与两类分发链接 |

Prism 旁路索引为每个 Release 保存 SHA-1、SHA-512、大小与相对路径。动态 `.mrpack` 把这些值写入 Modrinth 索引，并把文件环境标记为客户端必需、服务端不适用。Minecraft 与加载器声明由 HMCL addon 映射到 Modrinth dependency 名称；无法映射的声明会阻断生成。

## 5. 管理与运行行为

WPF 管理器采用上下两个可调整面板：上方维护稳定 Channel 与分发链接，下方维护 ZIP 候选版本。候选经过长按拖动到目标 Channel 后才发布，发布时保存 Release ID 与元数据快照。

管理器支持候选登记、源 ZIP 选择、只读检查、单个或批量转换、配置保存、过滤规则编辑以及本地服务启停。转换在后台 PowerShell 运行空间执行，界面通过进度与结果回调保持响应。

批量转换按候选逐项执行；单项失败写入结构化日志并继续其余候选。原型不会轮询收件箱，不会自动转换未知 ZIP，也不会自动发布。

## 6. 可借鉴的产品与架构经验

Servitium 可以独立采用下列问题拆分与行为原则：

* 候选内容与稳定 Channel 分离，转换成功不等于发布；
* 发布是显式、可审计的原子指针切换；
* Release 不可变且内容寻址，相同内容可复用；
* 正式写入前提供只读检查、过滤结果与转换计划；
* HMCL 使用稳定 Channel URL，发布过程仍需防止旧客户端请求与新内容混合；
* Prism 更新索引指向不可变 Release URL，避免更新会话观察到两个版本；
* 无法识别的重要加载器组件必须阻断并给出明确问题，不能静默忽略；
* 批量转换允许单项失败但必须逐项记录结果；
* 收件箱只作为显式选择来源，未知文件不会被后台自动处理。

这些原则必须映射到 Servitium 既有的长操作、审计、授权、不可变 Release、原子 Channel、内容寻址存储与发布门禁中，不能平移原型的数据结构或脚本组织。

## 7. 必须重新设计的边界

该原型是本机验证工具，不是 Servitium 的生产实现。Servitium 不得继承下列限制：

* PowerShell 模块、WPF 管理器与同步 `HttpListener` 请求循环；
* JSON 文件作为并发配置数据库；
* 原型的函数、类型、配置字段、界面布局、文案与样式；
* 只检查部分路径形态的归档处理；
* 缺少 Servitium 统一身份、授权、CSRF、限流、安全审计与长操作契约的端点；
* 未达到 Servitium 对 ZIP Slip、链接条目、压缩炸弹、深度、路径长度、设备名、ADS、重解析点和磁盘配额的完整要求；
* 未完整实现 Servitium HMCL 内容端点要求的 `Range`、强 ETag、历史回退查找和统一缓存契约；
* 依赖本机相对目录、实际服务器名称或特定过滤清单的约定。

最终实现必须遵循 `docs/product/`、`docs/architecture/` 与 `../reference/protocol-hmcl_update.md`，并通过 Servitium 自己的安全测试与契约测试。

## 8. 开发中的参考方式

编码 Agent 在实现以下能力时，应按需前往 `本机配置 ReferencesPackPrototype` 实地考察：

* Prism 导出 ZIP 的结构识别与兼容性诊断；
* 候选内容、不可变 Release 与稳定 Channel 的职责分离；
* 只读检查、过滤预览、转换进度、批量失败隔离与显式发布；
* 同一 Release 同时派生 HMCL manifest 与 Prism 更新索引；
* 稳定 Channel URL 与不可变 Release 下载 URL 的组合方式。

考察时先记录当前证据文件的时间与哈希，只阅读回答当前问题所需的最小范围。真实 ZIP、发布物、日志、运行状态和备份视为用户数据，不得读取、复制、移动、解压、执行或用作自动测试样本；真实内容验证需另行授权。证据更新与决定变更规则见 `../reference/upstream-references.md`。

## 9. 净室重建原则

允许观察外部行为、记录数据流、状态转换、验证级别、文件格式事实与风险；禁止复制、翻译后复制、移植或机械改写该原型的 PowerShell、JavaScript、HTML、CSS、配置内容、界面文案、布局细节、实际包名、服务器数据、过滤清单与品牌表达。

Servitium 使用自身的领域模型、契约、错误码、存储结构、算法实现、Fluent 2 界面与本地化文案重新实现同类能力。

## 10. 相关文档

* `../reference/upstream-references.md`
* `../reference/protocol-hmcl_update.md`
* `../research/investigation-hmcl_client.md`
* `../reference/format-prism_export.md`
* `../design/mechanism-hmcl_distribution.md`
* `../design/security-threat_model.md`
* [当前路线](../project/roadmap-development.md)

## 结论

采用的产品决定见项目范围和对应设计；不复制参考实现表达，不将历史探针当作当前联调。
