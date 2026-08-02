# Prism Launcher 实例导出格式

证据快照：`PRISM@7174c26a9`，验证方式为 `[SOURCE]`。本文档记录 Prism Launcher“导出实例”产生的原生 ZIP 结构，供 Servitium 识别与转换该格式时使用。

Prism Launcher 另有 Modrinth 与 CurseForge 导出功能；它们生成平台格式的整合包，不属于本文档所称的 Prism 原生实例 ZIP。快照详情、核心证据路径和取证边界见 `source-evidence-index.md`。

## 1. 导出入口与归档方式

“导出实例”操作打开文件树，以实例根目录为起点选择需要写入归档的文件。输出文件固定为 `.zip`，归档条目保留相对于实例根的路径，不添加额外顶层前缀。

导出前，非内置实例图标会被保存到实例根，以便归档携带图标。写入 ZIP 时启用符号链接跟随：条目名保留原相对路径，写入的是链接目标内容，而非符号链接条目。Servitium 仍必须把这类输入视为不可信归档，不能因为它由 Prism Launcher 生成就跳过安全检查。

## 2. 实例 ZIP 的顶层结构

当前 Minecraft 实例的原生导出包典型结构如下：

```text
instance.cfg
mmc-pack.json
.packignore                 可选
patches/                    可选
minecraft/                  通常的游戏目录
或 .minecraft/             兼容的旧游戏目录
<实例图标>                可选
<其他被选中的实例根文件> 可选
```

| 路径 | 作用 | 转换时的处置 |
| --- | --- | --- |
| `instance.cfg` | INI 格式的实例设置；Prism 导入器以该文件识别 MultiMC/Prism 实例 ZIP | 只作为结构标记与有限诊断输入，不继承本地启动设置 |
| `mmc-pack.json` | Minecraft 与加载器组件清单 | 解析格式版本、组件 `uid`、版本与标志 |
| `minecraft/` / `.minecraft/` | 客户端游戏工作目录 | 经路径安全与过滤预览后，作为发布内容候选 |
| `patches/` | 可选的自定义组件补丁 | 用于诊断是否存在非标准组件，不执行、不直接转换为启动逻辑 |
| `.packignore` | 导出对话框保存的用户排除路径 | 不作为 Servitium 过滤规则，不进入 Release |
| 图标与其他根文件 | 实例本地元数据或可选内容 | 默认不进入 Release；只在明确产品规则下提取元数据 |

Prism 自身的导入器会在 ZIP 中递归查找 `instance.cfg`，并把它的父目录当作实例根。因此，经其他工具重新打包后，上述结构可能位于一层外包目录中。Servitium 需要先唯一定位实例根，再解析其他内容；出现多个候选根时必须拒绝自动选择。

## 3. `mmc-pack.json`

当前格式的根对象包含：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `formatVersion` | 整数 | 当前为 `1`；Prism 对其他值报格式错误 |
| `components` | 数组 | 按加载顺序保存的组件条目 |

每个组件条目的字段：

| 字段 | 必需性 | 含义 |
| --- | --- | --- |
| `uid` | 必需 | 组件标识符 |
| `version` | 可选 | 选定版本；缺失时不能仅凭缓存名称推导发布版本 |
| `dependencyOnly` | 可选布尔 | 该组件仅因依赖关系存在 |
| `important` | 可选布尔 | 关键组件；无法识别时不得静默忽略 |
| `disabled` | 可选布尔 | 组件被停用 |
| `cachedVersion`、`cachedName`、`cachedRequires`、`cachedConflicts`、`cachedVolatile` | 可选 | Prism 的缓存元数据，不是 Servitium 的权威版本来源 |

与 Servitium 当前范围直接相关的 `uid` 包括：

| `uid` | 含义 |
| --- | --- |
| `net.minecraft` | Minecraft |
| `net.minecraftforge` | Forge |
| `net.neoforged` | NeoForge |
| `net.fabricmc.fabric-loader` | Fabric Loader |
| `org.quiltmc.quilt-loader` | Quilt Loader |

其他组件需要逐项判定是可安全忽略、仅用于诊断，还是必须阻断转换。未识别的关键组件、自定义补丁或缺少必需版本的加载器应返回明确诊断，不产生可发布 Release。

## 4. `instance.cfg` 的信任边界

`instance.cfg` 是实例级 INI 设置容器，可以包含名称、图标、时间统计、Java 路径与参数、内存、窗口、环境变量、启动前后命令、服务器地址、账户选择和托管整合包标识等本地状态。它不是一份为服务端分发设计的可移植 manifest。

Servitium 不得执行、保存或转发其中的命令、本机路径、账户标识、环境变量或其他启动器状态。显示名称与图标也只能作为导入预览，最终元数据由用户确认并写入 Servitium 自身契约。

## 5. 游戏目录与内容选择

`MinecraftInstance::gameRoot()` 在 `.minecraft/` 存在而 `minecraft/` 不存在时使用前者，其余情况使用 `minecraft/`。导出对话框默认排除游戏目录下的 `logs/`、`crash-reports/`、`.cache/`、`.fabric/` 与 `.quilt/`，同时排除 `.DS_Store`、`thumbs.db` 与 `Thumbs.db`。用户还可在文件树中改变选择，这些选择保存到 `.packignore`。

因此，Prism 实例 ZIP 是用户可编辑的实例快照，不是内容已经净化的发布包。Servitium 必须独立生成过滤预览，将候选文件分为纳入、排除和阻断三类；不信任 `.packignore`，也不因为文件被 Prism 选中就直接发布。

## 6. 与其他 Prism 导出功能的区别

| Prism 操作 | 输出 | 识别标记 | 本文档是否覆盖 |
| --- | --- | --- | --- |
| 导出实例 | `.zip` | `instance.cfg` + `mmc-pack.json` + 游戏目录 | 是 |
| 导出 Modrinth 包 | `.mrpack` 或 `.zip` | `modrinth.index.json` + `overrides/` | 否 |
| 导出 CurseForge 包 | `.zip` | `manifest.json` 及平台规定的覆盖目录 | 否 |

Prism 的导入器也会区分这三类标记。Servitium 不能用同一解析器猜测它们的语义；未列入产品范围的格式应返回稳定的不支持错误。

## 7. Servitium 的识别与转换约束

1. 在通过通用归档安全检查后，搜索唯一的 `instance.cfg`；零个或多个候选都拒绝。
2. 要求同一实例根下存在 `mmc-pack.json`，并严格校验 `formatVersion` 与 `components`。
3. `minecraft/` 与 `.minecraft/` 只允许唯一个成为游戏根；两者同时存在时要求用户解决歧义。
4. 加载器信息只从已校验的组件 `uid` 与 `version` 映射；不从文件名、缓存显示名或模组内容猜测。
5. `instance.cfg`、`.packignore`、`mmc-pack.json`、`patches/`、图标与其他启动器元数据不进入 HMCL Release。
6. 转换前输出一份不写入的计划，列出实例根、游戏根、组件、纳入文件、排除文件、阻断问题与将要使用的元数据。
7. 解析、过滤、写入 Release 与发布 Channel 是独立步骤；转换成功不自动发布。

## 8. 证据与许可边界

本快照只通过源码调用链确认格式和导出行为，没有构建或运行 Prism Launcher，也没有生成或打开用户实例包。

Prism Launcher 代码受 GPL-3.0-only 及文件内标注的兼容许可声明约束。Servitium 只使用上述可观察格式事实，不复用其导出器、导入器、过滤器、配置解析器或界面实现。通用取证与净室规则见 `docs/reference/source-evidence-index.md`。

## 9. 相关文档

* `docs/reference/source-evidence-index.md`
* `docs/reference/local-pack-publishing-prototype.md`
* `docs/reference/hmcl-auto-update-protocol.md`
* `docs/product/hmcl-distribution-specification.md`
* `docs/architecture/security-model.md`
