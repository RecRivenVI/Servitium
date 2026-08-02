# 本地化规格

## 1. 范围

首发语言：`zh-Hans`（简体中文）与 `en-US`（美式英语）。两者地位相同，不存在「主语言 + 翻译」的关系；任何文案变更必须同时提供两种语言。

默认语言：系统首次初始化时按 Windows 系统区域推断（中文区域 → `zh-Hans`，其余 → `en-US`），之后可在系统设置中修改，用户可覆盖为个人语言。

## 2. 分层

| 层 | 语言处置 |
| --- | --- |
| `Servitium.Web` | 客户端本地化 |
| `Servitium.Desktop` | 客户端本地化 |
| `Servitium.Server` REST 响应 | **不本地化**：返回稳定错误码与结构化参数，由客户端渲染 |
| `Servitium.Server` / `Servitium.Agent` 日志 | 固定 `en-US` |
| 诊断导出 | 固定 `en-US` |
| Windows 服务显示名与描述 | 安装时按系统区域选择 |
| MSI 安装界面 | 提供 `zh-Hans` 与 `en-US` 两套界面语言 |
| Windows 系统通知 | 按 Desktop 的当前语言 |
| HMCL manifest 内容 | 由运营者填写，Servitium 不翻译 |

## 3. 资源来源

**单一事实来源**：`Servitium.Contracts` 项目中维护一组与技术栈无关的文案资源文件（每语言一个 JSON 文件），构建时分别生成：

* Web 侧的 TypeScript 资源模块；
* Desktop 侧的 `.resw` 资源文件。

理由：`REQ-I18N-002` 要求双端文案一致。若两端各自维护译文，一致性无法用自动化守护。

生成是构建步骤的一部分，生成产物不手工编辑，且在 `eng/Validate.ps1` 中校验「生成产物与来源一致」（`REQ-PLT` 生成代码一致性门禁）。

## 4. 键命名

```text
<区域>.<页面或组件>.<元素>[.<变体>]
```

规则：

* 全小写，段间用 `.`，段内用 `-` 连接多词；
* 区域取值固定：`common`、`auth`、`node`、`instance`、`terminal`、`file`、`config`、`mod`、`schedule`、`user`、`dashboard`、`settings`、`hmcl`、`desktop`、`error`；
* 错误码文案的键固定为 `error.<稳定错误码>`；
* 不允许使用哈希或序号作为键名。

示例：

```text
instance.list.title
instance.list.empty-description
instance.action.force-terminate
instance.action.force-terminate.confirm-body
error.instance-not-stopped
```

## 5. 复数与参数

* 参数使用具名占位符 `{name}`，不使用位置占位符；
* 复数使用 ICU MessageFormat 的 `plural` 分类；`zh-Hans` 只需 `other`，`en-US` 需 `one` 与 `other`；
* 日期、时间、数字、字节大小、时长一律通过格式化 API 呈现，不在文案中硬编码格式。

## 6. 文案约定

### 6.1 `zh-Hans`

* 使用简体中文与全角标点；中英文之间不额外加空格（由样式层处理视觉间距）；
* 技术专有名词保留英文原形：`Java`、`Minecraft`、`Windows`、`HMCL`、`JAR`、`RCON`、`TOTP`、`OIDC`、`SHA-1`、`ZIP`、`Agent`；
* 产品与组件名不翻译：`Servitium`、`Servitium.Server`、`Servitium.Agent`、`Servitium.Desktop`、`Servitium.Web`；
* 动作按钮使用动词或动宾短语，不使用「请」字开头；
* 破坏性动作的确认正文必须写明「会发生什么」与「是否可撤销」；
* 不使用感叹号，不使用拟人化表述。

### 6.2 `en-US`

* 句子式大写（sentence case），不使用标题式大写；
* 按钮使用祈使句动词；
* 与 `zh-Hans` 表达同一含义，不做本地化改写（例如不把「强制终止」写成 "Kill"，统一为 "Force stop"）。

### 6.3 术语表

下列术语在两种语言中的对应固定，不得随上下文变化：

| 概念 | `zh-Hans` | `en-US` |
| --- | --- | --- |
| Node | 节点 | Node |
| Agent | Agent | Agent |
| Instance | 实例 | Instance |
| Java runtime | Java 运行时 | Java runtime |
| Working directory | 工作目录 | Working directory |
| Terminal | 控制台 | Console |
| Schedule / task | 计划任务 | Scheduled task |
| Action | 动作 | Action |
| Mod | 模组 | Mod |
| Plugin | 插件 | Plugin |
| Channel | 通道 | Channel |
| Release | 发布版本 | Release |
| Archive | 归档 | Archive |
| Operation | 操作 | Operation |
| Audit log | 安全审计日志 | Audit log |
| Operation log | 操作日志 | Activity log |
| Force stop | 强制终止 | Force stop |
| Unregister | 删除登记 | Unregister |
| Delete files | 删除工作目录 | Delete working directory |
| Register token | 注册令牌 | Registration token |
| Credential rotation | 凭据轮换 | Credential rotation |
| Owner | 所有者 | Owner |
| Administrator | 管理员 | Administrator |
| User | 普通用户 | User |
| Disabled | 已禁用 | Disabled |

术语表以数据文件形式维护，文案审查工具据此检查一致性。

## 7. 完备性与质量门禁

`eng/Validate.ps1` 中的本地化检查必须包含：

| 检查 | 失败处理 |
| --- | --- |
| 两种语言的键集合完全一致 | 构建失败 |
| 不存在空字符串译文 | 构建失败 |
| 不存在未被任何代码引用的键（孤儿键） | 警告，超过阈值时失败 |
| 不存在代码中引用但资源中不存在的键 | 构建失败 |
| 占位符集合在两种语言中一致 | 构建失败 |
| 术语表一致性 | 警告 |
| 生成产物与来源文件一致 | 构建失败 |

## 8. 时区与格式

* 服务端一律以 UTC 存储与传输；
* 客户端按本机时区展示，并在悬浮提示中给出 UTC 值；
* 系统设置中可配置「展示时区」用于服务端生成的报表与导出；
* 字节大小使用二进制单位（KiB、MiB、GiB），网络速率使用十进制单位（kB/s、MB/s），两者在两种语言中一致。

## 9. 可访问性相关文案

* 所有仅图标的按钮必须有本地化的可访问名称；
* 状态徽章的颜色必须配合文本，不以颜色单独表达状态；
* 表格列头、表单标签、错误提示均需本地化；
* 屏幕阅读器专用文本使用 `.sr` 后缀的键（例如 `instance.status.running.sr`）。

## 10. 新增语言的流程

首发后新增语言时：

1. 复制 `en-US` 资源文件作为起点；
2. 在语言清单中登记语言代码与显示名；
3. 补齐全部键；
4. 通过第 7 节的全部门禁；
5. 更新 `docs/records/decision-log.md`。

首发版本不新增其他语言。
