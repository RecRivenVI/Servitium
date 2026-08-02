# 总体开发计划

本文件定义 Servitium 的阶段划分、顺序、门禁与产物。每个阶段的详细规格在 `docs/development/phases/` 中。

**本文件不使用完成复选框保存实时进度。** 实时状态在 `docs/records/current-status.md`。

## 1. 阶段顺序

```text
00 文档基线
    ↓
10 后端工程基础
    ↓
11 Domain、Application 与 Contracts
    ↓
12 Server、身份与授权
    ↓
13 Agent、节点、Java 与实例
    ↓
14 生命周期、终端与指标
    ↓
15 文件、配置与模组
    ↓
16 计划任务、用户、审计与设置
    ↓
17 HMCL 分发后端
    ↓
18 后端完整自主验收   ← 硬门禁：未通过不得开始正式前端开发
    ↓
20 前端共享基础
    ↓
21 Web 前端
    ↓
22 Desktop 前端
    ↓
23 双端集成
    ↓
24 用户协作验收       ← 硬门禁：用户未明确确认不得标记通过
    ↓
等待用户明确启动交付   ← 交付是独立任务
```

## 2. 阶段总表

| 编号 | 名称 | 文档 | 主要产物 | 前置门禁 |
| --- | --- | --- | --- | --- |
| 00 | 文档基线 | `phases/00-documentation-baseline.md` | `docs/`、`README.md`、`AGENTS.md` | 无 |
| 10 | 后端工程基础 | `phases/10-backend-foundation.md` | 解决方案、构建配置、`eng/`、`artifacts/`、测试骨架 | 阶段 00 完成 |
| 11 | Domain、Application 与 Contracts | `phases/11-backend-domain-application-and-contracts.md` | 三个项目及其测试 | 阶段 10 完成 |
| 12 | Server、身份与授权 | `phases/12-backend-server-identity-and-authorization.md` | `Servitium.Server`、`Servitium.Infrastructure`、认证授权、REST 骨架、SignalR 事件 | 阶段 11 完成 |
| 13 | Agent、节点、Java 与实例 | `phases/13-backend-agent-nodes-java-and-instances.md` | `Servitium.Agent`、gRPC 通道、节点注册、Java、实例登记 | 阶段 12 完成 |
| 14 | 生命周期、终端与指标 | `phases/14-backend-lifecycle-terminal-and-metrics.md` | 进程与 Job Object、ConPTY、终端 Hub、指标 | 阶段 13 完成 |
| 15 | 文件、配置与模组 | `phases/15-backend-files-configuration-and-mods.md` | 文件流、归档、配置无损往返、模组解析 | 阶段 14 完成 |
| 16 | 计划任务、用户、审计与设置 | `phases/16-backend-scheduling-users-audit-and-settings.md` | 本地调度、用户与授权、双日志流、系统设置、仪表盘布局 | 阶段 15 完成 |
| 17 | HMCL 分发后端 | `phases/17-backend-hmcl-distribution.md` | Channel/Release/内容库、分发端点、发布与回滚 | 阶段 16 完成 |
| 18 | 后端完整自主验收 | `phases/18-backend-acceptance.md` | 完整验证通过、验收矩阵、追溯矩阵 | 阶段 17 完成 |
| 20 | 前端共享基础 | `phases/20-frontend-foundation.md` | `Servitium.Client`、界面契约、文案资源、生成客户端、共享组件规范落地 | **阶段 18 通过** |
| 21 | Web 前端 | `phases/21-web-frontend.md` | `Servitium.Web` 全部页面 | 阶段 20 完成 |
| 22 | Desktop 前端 | `phases/22-desktop-frontend.md` | `Servitium.Desktop` 全部页面、托盘、通知、本机换票 | 阶段 21 完成 |
| 23 | 双端集成 | `phases/23-frontend-integration.md` | 一致性测试、端到端、性能与无障碍 | 阶段 22 完成 |
| 24 | 用户协作验收 | `phases/24-user-acceptance.md` | 用户在可调试环境完成测试并确认 | 阶段 23 完成 |

## 3. 门禁定义

### 3.1 通用门禁

每个阶段进入下一阶段前必须满足：

1. 阶段文档的「完成条件」逐条满足；
2. `eng/Validate.ps1` 中该阶段涉及的检查全部通过；
3. 该阶段引入的需求在 `docs/testing/requirements-traceability.md` 中有实现与测试的映射；
4. `docs/records/current-status.md` 已更新；
5. 阶段文档的「禁止进入下一阶段的条件」全部不成立。

### 3.2 硬门禁：阶段 18

后端完整自主验收未通过时，**不得开始正式前端开发**（阶段 20 及以后）。

判定：

* `eng/Validate.ps1` 除 UI/端到端/前端相关阶段外全部通过；
* `docs/testing/backend-acceptance.md` 的全部检查项完成且有证据；
* 容量测试在容量目标下通过；
* 全部 `SEC-*` 安全测试通过；
* 真实 HMCL 客户端的协议级验证通过（可用命令行方式驱动，界面级验证留到阶段 24）。

允许的例外：为了验证后端而编写的最小化测试页面或脚本，不构成「正式前端开发」。

### 3.3 硬门禁：阶段 24

用户未明确确认通过时，不得标记用户验收完成，也不得进入交付。

## 4. 阶段产物与需求覆盖

| 阶段 | 覆盖的需求域 |
| --- | --- |
| 10 | — （工程基础） |
| 11 | `REQ-PLT-001`、领域规则相关的 `REQ-INS-*`、`REQ-IDN-030`、`REQ-USR-004` 的领域部分 |
| 12 | `REQ-IDN-*`、`REQ-PLT-010`、`REQ-PLT-021`、`REQ-PLT-022`、`REQ-PLT-030`、`REQ-SEC-010`、`REQ-SEC-020`、`REQ-SEC-021`、`REQ-SEC-030`（OIDC 入口）、`REQ-SEC-060`、`REQ-SEC-070` |
| 13 | `REQ-NOD-*`、`REQ-JRE-*`、`REQ-INS-001` … `REQ-INS-030`、`REQ-PLT-040`、`REQ-SEC-050` 的一部分 |
| 14 | `REQ-INS-040` … `REQ-INS-081`、`REQ-TRM-*`、`REQ-NOD-002` |
| 15 | `REQ-FIL-*`、`REQ-CFG-*`、`REQ-MOD-*`、`REQ-SEC-030`（URL 下载入口）、`REQ-SEC-050` |
| 16 | `REQ-SCH-*`、`REQ-USR-001` … `REQ-USR-062`、`REQ-SEC-080` |
| 17 | `REQ-HMC-*` |
| 18 | 全部后端需求的验收 |
| 20 | `REQ-I18N-*`、`REQ-DSK-050` 的契约基础 |
| 21 | 全部界面需求的 Web 侧 |
| 22 | `REQ-DSK-*`、全部界面需求的 Desktop 侧 |
| 23 | `REQ-DSK-050`、性能与无障碍 |
| 24 | 用户验收 |

完整映射见 `docs/testing/requirements-traceability.md`。

## 5. 阶段文档结构

每个阶段文档包含固定章节：

```text
1  阶段目的
2  需求编号
3  前置条件
4  范围
5  非目标
6  需要创建的项目或目录
7  需要实现的类型或模块
8  数据模型
9  API 或事件
10 实施顺序
11 关键算法
12 Windows 平台事项
13 安全要求
14 自动化测试
15 集成测试
16 人工测试
17 调试启动方式
18 验收责任方
19 完成条件
20 禁止提前进入下一阶段的条件
21 需要更新的动态记录
```

不适用的章节写「无」，不省略章节。

## 6. 工作量与拆分原则

每个阶段的设计目标：

* 目标单一；
* 可在一个连续的工作周期内完成；
* 不要求同时进行大量未拆分的研究、架构设计与实现；
* 输入与输出明确；
* 有可独立运行的验证。

若某阶段在实际执行中被发现过大，按变更请求流程提出拆分建议，**不得自行重排或合并阶段**。

## 7. 并行与返工

| 情形 | 处置 |
| --- | --- |
| 阶段之间 | 严格串行，不并行 |
| 阶段内部 | 按「实施顺序」串行 |
| 发现前一阶段的缺陷 | 立即修复并补测试，记录到 `problem-log.md`；不推迟到后续阶段 |
| 发现规格缺陷 | 变更请求流程 |
| 前端阶段发现后端缺陷 | 修复后端并重跑后端验收的相关部分 |

## 8. 交付

**开发完成不自动进入交付。**

交付启动条件（全部满足）：

1. 阶段 18 通过；
2. 阶段 23 的 Agent 自测通过；
3. 用户在测试环境完成测试（阶段 24）；
4. 用户明确确认验收通过；
5. 用户随后明确要求开始交付。

交付流程见 `docs/delivery/delivery-plan.md`。

## 9. 阶段编号规则

* `00`：文档基线；
* `1x`：后端；
* `2x`：前端与验收；
* 编号不复用；
* 新增阶段使用未占用的编号并同步更新本表与 `docs/development/phases/` 的文件名（受保护文档变更流程）。
