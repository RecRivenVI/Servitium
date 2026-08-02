# 构建与验证

## 1. 验证入口

```text
eng/Validate.ps1
```

该脚本是 Servitium 的**唯一完整验证入口**。后端自主验收、前端自主检查、交付前门禁均使用同一入口。

脚本在阶段 10 创建，随后由各阶段登记新增的验证能力。默认运行当前仓库已登记的全部检查；后端门禁、前端门禁与交付门禁使用固定 Profile，并额外断言各自要求的检查已经登记，缺项直接失败。

### 1.1 契约

| 项 | 规范 |
| --- | --- |
| 退出码 | 0 表示全部阶段通过；非 0 表示失败 |
| 输出 | 每阶段输出「阶段名 / 结果 / 耗时」；失败阶段输出完整错误 |
| 汇总 | 结束时输出阶段结果表 |
| 报告 | 写入 `artifacts/test-results/validate-<时间戳>/`，含各阶段日志、测试结果、覆盖率、包体积报告 |
| 参数 | `-Configuration`（默认 `Release`）、`-Profile`（`Development` / `BackendGate` / `FrontendGate` / `Delivery`）、`-Stages`（逗号分隔的子集，仅用于失败诊断与局部重跑） |
| 门禁限制 | 带 `-Stages` 的结果不是完整验证结果，不得作为阶段或交付门禁证据 |
| **禁止** | 不得提供跳过环境、测试或权限检查并仍返回成功的参数；已登记检查未执行即整体失败 |

### 1.2 阶段列表

按顺序执行，前一阶段失败即中止（除非用 `-Stages` 进行局部诊断）。下表同时规定每项检查首次登记的开发阶段；在此之前该检查尚未进入验证清单，不输出「跳过」或「无内容」。一旦登记，默认完整运行与其适用的门禁 Profile 必须执行它。

| # | 阶段 | 内容 | 首次登记阶段 | 失败即中止 |
| --- | --- | --- | --- | --- |
| 1 | 环境检查 | 校验当前 Profile 必需的组件与版本 | 10；后续扩展 | 是 |
| 2 | 锁定依赖还原 | `dotnet restore --locked-mode`；Web 建立后增加 `npm ci` | 10；20 扩展 | 是 |
| 3 | 生成代码一致性 | 在临时目录重新生成并逐文件比较 OpenAPI 客户端、protobuf、文案资源与界面契约产物 | 10；20 扩展 | 是 |
| 4 | Release 构建 | `dotnet build -c Release`；Web 建立后增加 `npm run build` | 10；21 扩展 | 是 |
| 5 | 分析器 | 构建期分析器（`TreatWarningsAsErrors`）；额外的 `dotnet build /p:RunAnalyzers=true` | 10 | 是 |
| 6 | 格式 | `dotnet format --verify-no-changes` | 10 | 是 |
| 7 | Web lint | `eslint --max-warnings=0`；`prettier --check` | 21 | 是 |
| 8 | TypeScript 检查 | `tsc --noEmit` | 20 | 是 |
| 9 | 单元测试 | 分类 `Unit` | 10 | 是 |
| 10 | 架构测试 | 分类 `Architecture` | 10 | 是 |
| 11 | 集成测试 | 分类 `Integration` | 10 | 是 |
| 12 | 契约测试 | 分类 `Contract`；OpenAPI 兼容性回归；protobuf 兼容性 | 12；20 扩展 | 是 |
| 13 | 安全测试 | 分类 `Security` | 12 | 是 |
| 14 | 容量测试 | 分类 `Capacity` | 18 | 是 |
| 15 | UI 测试 | 分类 `Ui`（WinUI Automation） | 22 | 是 |
| 16 | 端到端测试 | 分类 `EndToEnd`（真实 Server/Agent/夹具；HMCL；前端建立后增加 Playwright） | 18；21 扩展 | 是 |
| 17 | 覆盖率门禁 | `Domain`/`Application` ≥ 85%；整体 ≥ 70% | 10 | 是 |
| 18 | 依赖漏洞 | `dotnet list package --vulnerable --include-transitive`；Web 建立后增加 `npm audit --audit-level=high` | 10；20 扩展 | 是 |
| 19 | 包体积 | Web 初始 chunk ≤ 预算 | 21 | 是 |
| 20 | 本地化一致性 | 键集合、占位符、孤儿键、生成产物 | 20 | 是 |
| 21 | 文档一致性 | 见第 4 节 | 10 | 是 |
| 22 | 需要提升权限的验收 | Windows 服务、恢复策略、ACL、防火墙；Delivery Profile 再加入 MSI 场景（见 1.3） | 18；交付扩展 | 是 |

Profile 的完整性要求：

| Profile | 必须执行的已登记检查 |
| --- | --- |
| `Development` | 当前仓库已登记的全部检查 |
| `BackendGate` | 1–6、9–14、16–18、21–22；7、8、15、19、20 尚属前端能力 |
| `FrontendGate` | 1–22 全部 |
| `Delivery` | 1–22 全部，并包含第 22 阶段的 MSI 安装、升级、卸载场景 |

### 1.3 需要提升权限的阶段

阶段 22 在没有管理员权限时**不得跳过**。行为：

* 检测当前进程是否具有管理员权限；
* 无权限时：输出「权限阻塞」报告（阻塞阶段、需要执行的命令、原因、用户需要做什么、系统影响、恢复步骤、清理方式），并以非 0 退出码结束；
* 有权限时：执行当前 Profile 登记的服务安装/卸载、恢复策略配置、ACL 校验与防火墙规则；`Delivery` Profile 另执行 MSI 安装、升级与卸载测试；结束后清理。

## 2. 构建

### 2.1 输出

全部产物输出到 `artifacts/`：

```text
artifacts/
├── bin/<项目>/<配置>/
├── obj/<项目>/<配置>/
├── web/                     Web 构建产物（随后复制到 Server 的 wwwroot）
├── openapi/swagger.json
├── test-fixtures/
├── test-results/
├── installers/
└── packages/
```

### 2.2 版本号

| 项 | 规范 |
| --- | --- |
| 格式 | `<主>.<次>.<修订>[-<预发布>]+<构建元数据>` |
| 来源 | `Directory.Build.props` 中的基版本 + CI 构建号或本地 `0` |
| 传播 | 程序集版本、文件版本、`InformationalVersion`、Web 的 `__APP_VERSION__`、MSI `ProductVersion` |
| 展示 | 系统信息与「关于」中展示完整版本与构建号 |

首个正式版本号在交付阶段确定。开发期使用 `0.1.0-dev`。

### 2.3 Web 构建产物的接入

`npm run build` 输出到 `artifacts/web/`，构建脚本随后复制到 `artifacts/bin/Servitium.Server/<配置>/wwwroot/`。Server 项目不在源码树中保留构建产物。

### 2.4 确定性

启用确定性构建与源链接。相同源码与相同 SDK 版本必须产生相同的程序集（除签名与时间戳外）。

## 3. 代码生成

`eng/Generate.ps1` 负责：

| 产物 | 来源 | 目标 |
| --- | --- | --- |
| `swagger.json` | 运行 `Servitium.Server` 的 OpenAPI 端点或使用生成任务 | `artifacts/openapi/swagger.json` |
| TypeScript API 客户端 | `swagger.json` | `src/Servitium.Web/src/api/` |
| protobuf C# | `src/Servitium.Contracts/Protos/*.proto` | 构建期生成 |
| 文案资源 | `src/Servitium.Contracts/Localization/*.json` | Web 的 TS 模块与 Desktop 的 `.resw` |
| 界面契约清单 | `src/Servitium.Contracts/Ui/*.json` | 两端测试用的类型化清单 |

`-Verify` 参数下：把全部生成结果写入独立临时目录，按目标清单比较文件集合与逐字节内容；存在缺失、多余或内容差异即失败，比较结束后删除临时目录。该过程不改写工作树，也不把用户已有的未提交修改误判为生成差异。

## 4. 文档一致性检查

阶段 10 建立并执行下列检查，后续阶段随文档与契约增长持续扩展。检查脚本位于 `eng/`，是仓库内的永久工程脚本。

| 检查 | 失败条件 |
| --- | --- |
| Markdown 内部链接 | 存在指向不存在文件或不存在锚点的链接 |
| 空文件 | `docs/` 下存在 0 字节或仅有标题的文件 |
| 需求编号唯一 | `functional-specification.md` 中出现重复编号 |
| 需求编号被引用 | 存在被其他文档引用但未定义的 `REQ-*` 编号 |
| 阶段编号连续 | `docs/development/phases/` 中的文件名与 `master-plan.md` 的阶段列表一致 |
| 追溯矩阵覆盖 | `requirements-traceability.md` 中存在未映射到测试的需求（允许标注「计划中」，但不允许缺失行） |
| 验收矩阵覆盖 | `acceptance-matrix.md` 中存在未覆盖的验收项 |
| 占位词 | 正式产品文档（`product/`、`architecture/`、`design/`、`engineering/`、`development/phases/`）中出现「待研究」「待定」「TODO」「视情况」「可能使用」「参考 MCSManager」「参考 HMCL 实现」等模糊表述 |
| 命名规范 | `docs/` 下存在非小写或非 `kebab-case` 的文件或目录（`README.md`、`AGENTS.md` 除外） |
| 受保护文档变更 | 与基线快照比较，受保护文档被修改但 `change-requests.md` 中无对应的已批准条目 |

## 5. 测试执行

### 5.1 分类

见 `engineering-standards.md` 第 7.1 节。

### 5.2 并行

单元测试与架构测试并行执行；集成、安全、UI、端到端测试串行执行（避免端口与文件系统冲突）。

### 5.3 稳定性

| 要求 | 说明 |
| --- | --- |
| 无随机失败 | 连续 3 次完整运行必须全部通过 |
| 无 `Sleep` 等待 | 使用轮询 + 超时的显式等待 |
| 超时 | 每个测试有明确超时；超时失败信息包含当时的状态快照 |
| 清理 | 测试结束后清理临时目录与进程；失败时保留产物到 `artifacts/test-results/` 便于诊断 |
| 端口 | 使用随机可用端口 |

### 5.4 失败诊断产物

失败时保存：进程标准输出与错误、服务日志、数据库文件副本、Playwright 截图与追踪、终端缓冲快照、失败时刻的实例状态。

## 6. 覆盖率

| 范围 | 阈值 |
| --- | --- |
| `Servitium.Domain` | 行覆盖 ≥ 85% |
| `Servitium.Application` | 行覆盖 ≥ 85% |
| 整体 | 行覆盖 ≥ 70% |

排除：生成代码、`Program.cs`、互操作声明、迁移文件。

覆盖率不作为唯一质量指标；关键路径必须有针对性测试，即使覆盖率已达标。

## 7. 性能与容量

阶段 18 的容量测试在容量目标下运行至少 30 分钟：

| 断言 | 阈值 |
| --- | --- |
| 8 个 Agent 全部在线且心跳正常 | — |
| 64 个实例可创建、启动、停止 | — |
| 20 个并发会话可正常操作 | — |
| 8 个并发终端持续输出无丢失（除显式标记的背压丢弃外） | — |
| 控制平面工作集稳定 | 30 分钟内增长 < 20% |
| 句柄与连接数稳定 | 无单调增长 |
| 列表接口 P95 | < 300 毫秒 |
| 终端端到端延迟 P95 | < 200 毫秒 |

## 8. 构建性能预算

| 项 | 目标 |
| --- | --- |
| 增量构建 | < 30 秒 |
| 完整 Release 构建 | < 5 分钟 |
| 单元 + 架构测试 | < 3 分钟 |
| 完整 `Validate.ps1` | < 45 分钟（不含需要提升权限的阶段） |

超出目标不构成失败，但应记录到 `docs/records/problem-log.md`。

## 9. 与验收的关系

| 验收 | 使用的阶段 |
| --- | --- |
| 后端完整自主验收（阶段 18） | `BackendGate` 通过，且 `docs/testing/backend-acceptance.md` 的检查表全部完成 |
| 前端自主检查（阶段 23） | `FrontendGate` 通过（含 UI 与端到端） |
| 用户验收（阶段 24） | 在上述基础上由用户在可调试环境中完成 `docs/testing/user-testing-procedure.md` |
| 交付启动 | 用户验收通过 + 用户明确要求；交付前再跑一次含阶段 22 的完整验证 |

## 10. CI

阶段 00–24 **不创建** `.github/workflows/`。CI 工作流仅在用户于交付阶段明确选择后编写，见 `docs/delivery/delivery-plan.md`。

在此之前，`eng/Validate.ps1` 是唯一的验证手段，由开发者或编码 Agent 在本地执行。
