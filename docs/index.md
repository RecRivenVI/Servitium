# Servitium 文档索引

Servitium 是一套面向 Windows 的 Minecraft Java 服务端托管与整合内容分发平台。

本目录是 Servitium 的规范基线与证据入口。后续开发以受保护文档定义的产品范围和契约为准，并在实现细节、外部行为或版本差异需要核实时，按 `AGENTS.md` 前往明确参考路径进行有目标的实地考察。

## 1. 从哪里开始

| 你是 | 从这里开始 |
| --- | --- |
| 第一次接触本项目 | `product/product-definition.md` → `product/decision-baseline.md` |
| 即将开始编码 | `../AGENTS.md` → `development/development-workflow.md` → `development/master-plan.md` |
| 要实现某个功能 | `product/functional-specification.md` 找需求编号 → 对应的架构与设计文档 |
| 要实现界面 | `design/information-architecture.md` → `design/shared-ui-specification.md` |
| 要实现 HMCL 分发 | `reference/hmcl-auto-update-protocol.md` → `product/hmcl-distribution-specification.md` |
| 要识别或转换 Prism 导出包 | `reference/prism-instance-export-format.md` → `reference/local-pack-publishing-prototype.md` → `product/hmcl-distribution-specification.md` |
| 想知道某个功能为什么这样设计 | `reference/mcsmanager-feature-inventory.md` 或 `reference/mcsmanager-behavior-matrix.md` |
| 要写测试 | `testing/test-strategy.md` |
| 要交付 | `delivery/delivery-plan.md` |

## 2. 文档地图

### 2.1 产品 `product/`

定义 Servitium 应当是什么。

| 文档 | 内容 |
| --- | --- |
| `product-definition.md` | 产品陈述、组成、目标用户、原则、范围边界、运行形态、支持矩阵、容量目标 |
| `decision-baseline.md` | 18 条不可协商的固定决策与 13 条派生约束 |
| `functional-specification.md` | **需求基线**：262 条需求，含要求与验收 |
| `hmcl-distribution-specification.md` | 整合内容分发的产品规格：Channel、Release、Archive、发布与回滚 |
| `localization-specification.md` | `zh-Hans` 与 `en-US` 的资源结构、命名、术语表与门禁 |

### 2.2 参考证据 `reference/`

记录 MCSManager、HMCL、Prism Launcher 与本地整合包发布原型的已登记证据。各文档说明取证版本、验证级别、可观察事实与 Servitium 的对应取舍；开发中需要新证据时，按 `AGENTS.md` 核实明确参考路径。

| 文档 | 内容 |
| --- | --- |
| `source-evidence-index.md` | Git 提交与非 Git 文件快照、考察方法、净室边界、后续核实流程与引用格式 |
| `mcsmanager-feature-inventory.md` | 已登记快照上的 304 条功能清单与分类（采用 / 适配 / 排除 / 不适用） |
| `mcsmanager-behavior-matrix.md` | 已登记快照上的 17 个业务场景行为链与 Servitium 采用决定 |
| `mcsmanager-frontend-layout.md` | 已登记快照上的逐页界面结构证据与适配决定 |
| `hmcl-auto-update-protocol.md` | **协议权威规范**：manifest、路径、哈希、加载器、更新算法、HTTP 行为分级 |
| `hmcl-client-behavior.md` | 22 个客户端场景的行为与端到端测试依据 |
| `prism-instance-export-format.md` | Prism 原生实例 ZIP：导出方式、归档结构、组件清单、游戏目录与信任边界 |
| `local-pack-publishing-prototype.md` | 本地整合包发布原型：Prism 导出包识别、候选与 Channel 分离、不可变 Release、HMCL/Prism 双格式派生及净室边界 |

HMCL 协议的关键结论经过**运行验证**（用真实 HMCL 代码驱动的探针实测），验证过程见 `records/validation-log.md`。

### 2.3 架构 `architecture/`

定义系统怎么构成。

| 文档 | 内容 |
| --- | --- |
| `system-architecture.md` | 运行拓扑、职责划分、项目分层与依赖方向、数据流、可用性与降级、技术选型 |
| `data-and-storage.md` | 两个数据库、目录布局与 ACL、实体、约束、内容库、备份、保留期、脱敏 |
| `protocols-and-contracts.md` | REST、SignalR、gRPC、本机管道、HMCL 端点、错误码、幂等、分页、长操作、兼容性规则 |
| `security-model.md` | 41 个威胁及其应对与必须存在的测试 |
| `repository-and-solution-layout.md` | 目标目录布局、命名规则、各项目职责、目录创建时机 |

### 2.4 界面 `design/`

定义界面长什么样、怎么交互。

| 文档 | 内容 |
| --- | --- |
| `information-architecture.md` | **页面权威规格**：30 个页面的目的、权限、区块、字段、动作、状态、错误、空态 |
| `shared-ui-specification.md` | 设计令牌、栅格、组件语义、交互模式、键盘、无障碍、格式化、性能预算 |
| `desktop-specification.md` | WinUI 3 专项：生命周期、托盘、通知、本机免登、WebView2 约束、与 Web 的差异清单 |
| `web-specification.md` | React 专项：技术约束、目录结构、数据获取、终端与编辑器、响应式、代码质量 |

### 2.5 工程 `engineering/`

定义怎么写、怎么建、怎么验。

| 文档 | 内容 |
| --- | --- |
| `engineering-standards.md` | C#、TypeScript、数据库、gRPC、API、测试、日志、配置、并发、互操作的规范与强制手段 |
| `development-environment.md` | 必需组件、搭建、运行、调试、测试隔离、需要提升权限的操作 |
| `build-and-validation.md` | `eng/Validate.ps1` 的 22 个阶段、构建、代码生成、文档一致性检查、覆盖率与性能门禁 |

### 2.6 开发 `development/`

定义按什么顺序做。

| 文档 | 内容 |
| --- | --- |
| `development-workflow.md` | 执行循环、阅读顺序、参考路径实地考察、动态记录、受保护文档、权限阻塞流程、协作验收 |
| `master-plan.md` | 15 个阶段的顺序、门禁、产物与需求覆盖 |
| `risk-register.md` | 已识别风险、应对、触发信号与已接受的残余风险 |
| `phases/` | 15 份阶段文档，每份 21 个固定章节 |

阶段顺序：

```text
00 文档基线 → 10 后端工程基础 → 11 Domain/Application/Contracts
→ 12 Server/身份/授权 → 13 Agent/节点/Java/实例 → 14 生命周期/终端/指标
→ 15 文件/配置/模组 → 16 计划任务/用户/审计/设置 → 17 HMCL 分发后端
→ 18 后端完整自主验收（硬门禁）
→ 20 前端共享基础 → 21 Web 前端 → 22 Desktop 前端 → 23 双端集成
→ 24 用户协作验收（硬门禁）
→ 等待用户明确启动交付
```

### 2.7 测试 `testing/`

定义怎么验收。

| 文档 | 保护状态 | 内容 |
| --- | --- | --- |
| `test-strategy.md` | 受保护 | 原则、层次、编号体系、覆盖要求、平台矩阵、等待与超时 |
| `backend-acceptance.md` | 受保护 | 阶段 18 的 A–S 检查清单 |
| `frontend-acceptance.md` | 受保护 | 阶段 23 的 A–P 检查清单 |
| `user-testing-procedure.md` | 受保护 | 阶段 24 的用户测试流程与判定 |
| `acceptance-matrix.md` | **可更新** | 验收项状态与证据 |
| `requirements-traceability.md` | **可更新** | 需求 → 实现 → 测试 → 状态 |

### 2.8 记录 `records/`

**全部可持续更新。** 只记录事实、状态、证据与变更请求，不能用于绕过受保护规格。

| 文档 | 内容 |
| --- | --- |
| `current-status.md` | 当前阶段、门禁状态、下一步、阻塞项 |
| `development-log.md` | 按时间的实质进展、产物与验证 |
| `decision-log.md` | 实现级决策；含待决策清单 |
| `problem-log.md` | 问题、根因、处置、回归测试 |
| `validation-log.md` | 每次实际运行的验证及结果 |
| `change-requests.md` | 受保护文档的变更请求与用户裁决 |

### 2.9 交付 `delivery/`

| 文档 | 内容 |
| --- | --- |
| `delivery-plan.md` | 交付范围、启动条件、MSI、安装验证、数据保留、签名、标签与 Release |

**交付不会自动发生。** 需要用户在验收通过之后明确要求启动。

## 3. 编号体系

| 前缀 | 含义 | 定义位置 |
| --- | --- | --- |
| `REQ-*` | 需求 | `product/functional-specification.md` |
| `DEC-*` | 固定决策 | `product/decision-baseline.md` |
| `MCSM-*` | MCSManager 功能条目 | `reference/mcsmanager-feature-inventory.md` |
| `BM-*` | 行为链场景 | `reference/mcsmanager-behavior-matrix.md` |
| `HCB-*` | HMCL 客户端场景 | `reference/hmcl-client-behavior.md` |
| `THR-*` | 威胁 | `architecture/security-model.md` |
| `PG-*` | 页面 | `design/information-architecture.md` |
| `UX-*` | 交互模式 | `design/shared-ui-specification.md` |
| `RSK-*` | 风险 | `development/risk-register.md` |
| `BA-*` / `FA-*` / `U-*` | 验收项 | `testing/` 下对应文档 |
| `UNT-* ARC-* INT-* CTR-* SEC-* CAP-* REC-* E2E-* WEB-* DUT-* DUI-* X2E-* CON-* PRF-* A11Y-* LOC-*` | 测试 | `testing/test-strategy.md` 第 3 节 |
| `DL-*` / `PB-*` / `CR-*` | 决策 / 问题 / 变更请求 | `records/` 下对应文档 |

编号一经分配不再改变含义，作废时保留编号并标注。

## 4. 治理

| 文件 | 作用 |
| --- | --- |
| `../README.md` | 项目门面，只含长期有效内容 |
| `../AGENTS.md` | **后续编码 Agent 的约束**：规则优先级、必读顺序、门禁、命名、测试、安全、Git、交付启动门 |

受保护文档清单与变更流程见 `records/change-requests.md`。

## 5. 本文档集的边界

* 定义了 Servitium **应当是什么**与**怎么做**；
* 不包含产品代码、测试代码、工程脚本、安装器或 CI 配置；
* `reference/` 保存已登记版本的证据快照；开发过程中可按需实地考察，任何决定变化仍按变更流程处理；
* 实时状态在 `records/`，不在本文件也不在受保护文档中。
