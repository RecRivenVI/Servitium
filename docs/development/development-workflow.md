# 开发工作流

本文件定义编码 Agent 在 Servitium 上的工作方式。

## 1. 基本原则

1. **基线优先、证据常新**：产品范围、契约、阶段与验收以受保护文档为准；实现细节或外部行为存在疑问时，按第 4 节实地考察参考路径，以当前证据消除猜测。
2. **阶段串行**：按 `master-plan.md` 的固定顺序推进。未通过门禁不得进入下一阶段。
3. **后端优先**：后端未通过完整自主验收（阶段 18）前，不得开始正式前端开发。
4. **实际运行**：验收必须实际运行测试，不得只审查代码。
5. **不擅自变更基线**：受保护文档的变更走变更请求流程。
6. **默认不提交**：Git 提交需用户明确授权。

## 2. 每个阶段的执行循环

```text
1. 读取阶段文档（docs/development/phases/<阶段>.md）
2. 确认前置条件全部满足；不满足则停止并报告
3. 读取该阶段引用的规格文档
4. 明确当前实现需要核实的外部事实，按需实地考察参考路径
5. 按「实施顺序」逐步实现
6. 编写并运行该阶段要求的测试
7. 运行 eng/Validate.ps1（可用 -Stages 限定到相关阶段）
8. 更新动态记录（见第 5 节）
9. 核对「完成条件」
10. 核对「禁止进入下一阶段的条件」
11. 报告阶段结果
```

### 2.1 开始阶段前必须确认

* 当前分支为 `develop`；
* 工作树状态已知；
* 前一阶段的完成条件已在 `docs/records/current-status.md` 中记录为已完成；
* 本阶段的前置条件全部满足。

### 2.2 阶段内的实现顺序

阶段文档中的「实施顺序」是**强制顺序**，不得重排。理由是每一步的产物是下一步的输入，乱序会导致返工。

### 2.3 遇到阻塞

| 阻塞类型 | 处置 |
| --- | --- |
| 规格不明确 | 写入 `docs/records/change-requests.md`，继续推进不受影响的部分，等待用户裁决 |
| 与既有文档冲突 | 同上；**不得**自行修改受保护文档来消除冲突 |
| 缺少权限 | 见第 7 节 |
| 缺少环境组件 | 报告缺失项与获取方式，请求用户协助 |
| 外部依赖不可达 | 报告并请求协助；不得降级依赖版本或跳过依赖 |
| 测试失败且原因不明 | 记录到 `docs/records/problem-log.md`，继续排查；不得注释掉测试或降低断言 |

## 3. 阅读顺序

首次接手时按此顺序阅读：

```text
1. AGENTS.md
2. docs/index.md
3. docs/product/product-definition.md
4. docs/product/decision-baseline.md
5. docs/product/functional-specification.md
6. docs/architecture/system-architecture.md
7. docs/architecture/protocols-and-contracts.md
8. docs/architecture/security-model.md
9. docs/architecture/data-and-storage.md
10. docs/architecture/repository-and-solution-layout.md
11. docs/engineering/engineering-standards.md
12. docs/engineering/development-environment.md
13. docs/engineering/build-and-validation.md
14. docs/development/development-workflow.md
15. docs/development/master-plan.md
16. 当前阶段的 docs/development/phases/<阶段>.md
17. docs/records/current-status.md
```

按需查阅：

| 场景 | 文档 |
| --- | --- |
| 实现界面 | `docs/design/**` |
| 实现 HMCL 分发 | `docs/product/hmcl-distribution-specification.md`、`docs/reference/hmcl-auto-update-protocol.md`、`docs/reference/hmcl-client-behavior.md` |
| 确认某功能的来源与取舍 | `docs/reference/mcsmanager-feature-inventory.md`、`mcsmanager-behavior-matrix.md`；必要时核实 `D:\GitHubPublic\MCSManager` |
| 确认页面结构与交互依据 | `docs/reference/mcsmanager-frontend-layout.md`；必要时核实 `D:\GitHubPublic\MCSManager` |
| 识别 Prism 原生实例 ZIP | `docs/reference/prism-instance-export-format.md`；必要时核实 `D:\GitHubRecRivenVI\PrismLauncher` |
| 实现 Prism ZIP 转换与发布预览 | `docs/reference/local-pack-publishing-prototype.md`；必要时核实 `E:\Minecraft\MinecraftServersDuoyun\test` |
| 设计测试 | `docs/testing/**` |
| 本地化 | `docs/product/localization-specification.md` |

## 4. 参考路径实地考察

`docs/reference/**` 提供已登记的提交或文件快照。编码 Agent 在开发过程中应根据当前任务按需核实原始证据，尤其适用于下列情况：

* HMCL 协议字段、更新算法、HTTP 缓存或客户端边界需要确认；
* MCSManager 的功能分布、页面布局、交互流程、权限或多节点行为需要确认；
* Prism 原生实例 ZIP 的导出入口、目录结构、组件清单或默认排除内容需要确认；
* Prism ZIP 转换、候选与 Channel 分离、不可变 Release 或双格式派生行为需要确认；
* 参考文档没有覆盖当前问题，或文档、实现与运行结果相互冲突；
* Git 参考的版本或本地原型的证据文件变化可能影响兼容性判断或测试设计。

实地考察路径固定为：

```text
D:\GitHubPublic\HMCL
D:\GitHubPublic\MCSManager
D:\GitHubRecRivenVI\PrismLauncher
E:\Minecraft\MinecraftServersDuoyun\test
```

每次考察遵循以下流程：

1. 提出与当前实现直接相关的具体问题，避免无边界浏览；
2. 记录参考路径；Git 仓库记录分支、提交与工作树状态，非 Git 参考记录证据文件时间与清单哈希；
3. 先定位源码、路由、资源或协议调用链，静态证据不足时再做有边界的运行验证；
4. 四个参考路径保持只读，依赖安装、编译、探针与运行产物放到参考路径外的临时目录或隔离副本；本地原型中的真实 ZIP、Release、日志、运行状态和备份未经明确授权不读取或用作测试样本；
5. 在 `development-log.md` 或 `validation-log.md` 记录问题、证据路径、命令、观察结果与证据级别；
6. 新证据若要求改变受保护文档中的产品、协议、界面、阶段或安全决定，在 `change-requests.md` 登记并等待用户裁决。

考察不授予复制参考实现的许可。Agent 只能提取外部可观察行为和事实，Servitium 的代码、资源、样式、图标、文案、品牌表达与内部设计必须保持独立。

## 5. 动态记录

下列文件由编码 Agent 持续更新，记录事实与状态，**不能用于绕过受保护规格**。

| 文件 | 何时更新 | 内容 |
| --- | --- | --- |
| `docs/records/current-status.md` | 每个阶段开始与结束 | 当前阶段、已完成阶段、进行中的工作、下一步、阻塞项 |
| `docs/records/development-log.md` | 每次有实质进展 | 日期、阶段、做了什么、产物、验证方式与结果 |
| `docs/records/decision-log.md` | 做出实现级决策时 | 决策、备选、理由、影响、可逆性。**仅限规格未约束的实现细节**；触及基线的走变更请求 |
| `docs/records/problem-log.md` | 遇到问题时 | 现象、复现、影响、根因（已知时）、处置、状态 |
| `docs/records/validation-log.md` | 每次运行验证 | 时间、命令、环境、阶段结果、失败详情、产物位置 |
| `docs/records/change-requests.md` | 需要变更受保护文档时 | 当前要求、问题、建议、影响、状态、用户裁决 |
| `docs/testing/acceptance-matrix.md` | 完成验收项时 | 验收项、状态、证据 |
| `docs/testing/requirements-traceability.md` | 实现或测试需求时 | 需求 → 实现位置 → 测试 → 状态 |

记录使用绝对日期（`YYYY-MM-DD`），不使用「今天」「上周」等相对表述。

## 6. 受保护文档

下列内容**不得由编码 Agent 自行修改**：

```text
README.md
AGENTS.md
docs/index.md
docs/product/**
docs/reference/**
docs/architecture/**
docs/design/**
docs/engineering/**
docs/development/development-workflow.md
docs/development/master-plan.md
docs/development/risk-register.md
docs/development/phases/**
docs/testing/test-strategy.md
docs/testing/backend-acceptance.md
docs/testing/frontend-acceptance.md
docs/testing/user-testing-procedure.md
docs/delivery/delivery-plan.md
```

变更流程：

1. 在 `docs/records/change-requests.md` 中新建条目，写明**当前要求**、**问题**、**建议**、**影响**（涉及哪些文档、需求编号与阶段）；
2. 停止依赖该变更的工作，继续推进其余部分；
3. 等待用户决定；
4. 未经用户批准不得修改基线。

## 7. 权限阻塞流程

当阶段确实需要下列操作而当前会话无权限时：

* 安装 Windows 服务；
* 修改服务恢复策略；
* 设置系统 ACL；
* 写入受保护目录；
* 创建防火墙规则；
* 安装 MSI；
* 在干净系统执行安装测试。

**必须**停止相关阶段并请求用户协助。**不得**：跳过、伪造通过、用静态审查代替、降低安全要求、删除该验收项。

请求协助时必须给出：

```text
阻塞阶段：<阶段编号与名称>
需要执行的命令：<完整命令>
遇到的错误：<原始错误输出>
权限原因：<为什么需要提升权限>
用户需要做什么：<具体步骤>
系统影响：<会改变系统的哪些状态>
恢复步骤：<如何撤销>
清理方式：<测试后如何清理>
```

用户完成后，Agent 重新运行该阶段的验证并记录到 `docs/records/validation-log.md`。

## 8. 与用户协作的验收

### 8.1 何时请求用户参与

只有在下列时机：

* 阶段 24（用户协作验收）；
* 权限阻塞（第 7 节）；
* 变更请求需要裁决；
* 阶段文档中明确标注「需要用户确认」的检查点。

其余阶段由 Agent 自主完成，不打断用户。

### 8.2 请求测试时必须提供

**不得只说「请测试」。** 必须提供：

```text
当前阶段：
Agent 已完成的测试：<列表与结果>
已启动的进程：<组件、PID、数据目录>
访问地址：<URL>
测试账号：<用户名与密码>
测试数据：<已准备的实例、节点、Channel 等>
操作步骤：<编号步骤>
每步预期结果：<逐步说明>
日志位置：<路径>
已知限制：<当前未实现或已知问题>
停止方式：<如何终止全部进程>
需要反馈的结果：<希望用户确认的具体项>
```

### 8.3 验收判定

用户未明确确认通过时，**不得**标记用户验收完成。用户提出的问题记入 `docs/records/problem-log.md`，修复后重新请求验收。

## 9. Git

| 规则 | 说明 |
| --- | --- |
| 默认不提交 | 需用户明确授权 |
| 授权后 | 按独立、可验证单元提交；提交前必须通过对应测试 |
| 不得 | 未经授权推送、打标签、发布 |
| 不得提交 | 秘密、用户数据、构建产物、大型临时文件 |
| 分支 | 在 `develop` 上工作；不改写历史；不删除其他分支；不修改远程引用 |

提交信息格式见 `AGENTS.md`：只允许 `type(scope): 中文摘要` 形式的单行标题；`type` 使用规定的英文词，`scope` 必填并优先使用八个宏观中文范围中的一个，结构标点使用 ASCII 半角形式，中文摘要使用中文标点；禁止正文、脚注和 trailer。

## 10. 临时脚本

允许在工作过程中使用临时脚本进行检查与探查，但：

* 临时脚本不得作为永久文件保留在仓库中；
* 需要长期保留的检查逻辑必须放入 `eng/` 并纳入 `Validate.ps1`；
* 临时文件放在会话临时目录，不放在仓库内。

## 11. 完成阶段的报告格式

```text
阶段：<编号与名称>
状态：已完成 / 部分完成 / 阻塞
产物：<新增与修改的文件>
实现要点：<简述>
测试：<运行的命令与结果>
验证：<Validate.ps1 阶段结果>
更新的记录：<文件列表>
完成条件核对：<逐条>
未完成项与原因：<如有>
下一阶段的前置条件：<是否满足>
```

## 12. 不做的事

| 不做 | 理由 |
| --- | --- |
| 未经记录与裁决，用参考实现覆盖 Servitium 的产品、协议或界面决定 | 实地考察只提供事实证据，不构成变更授权 |
| 复制参考项目的源码、资源、样式、图标、文案或品牌元素 | 违反净室原则 |
| 修改受保护文档 | 需用户批准 |
| 跳过测试或降低断言 | 破坏验收有效性 |
| 用静态审查代替实际运行 | 明确禁止 |
| 在一个阶段中同时做大量未拆分的研究、架构与实现 | 阶段已按此原则拆分 |
| 提前进入下一阶段 | 门禁存在的意义 |
| 自动进入交付 | 交付是独立任务，需用户明确启动 |
| 创建 `.github/workflows/` | 仅在交付阶段用户明确选择后创建 |
