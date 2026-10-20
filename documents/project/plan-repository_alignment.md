# 仓库规范落实计划

## 目标

落实仓库规范 1.0.0，基于当前 develop 工作树整理规范、目录、文档和工具，完成同命令前后验证后提交并推送一个 Update。产品行为、界面和 Windows App SDK 版本不在本轮修改范围。

## 范围

### 三处主要风险

1. 移动后脚本、测试定位、命名空间及 WinUI 资源引用失效：先留存构建和测试基线；更新引用后使用相同命令比较，不把原有失败算作整理引入。
2. 运行产物混入产品源码或测试输入：按 Git 跟踪清单区分；保留 src、tests 中的正式源码、合成夹具与迁移；仅将产物、探针及历史证据移出，并登记清单。
3. 旧治理拆分丢失用户约束：逐节提取 Java 范围、净室、参考只读、外部目录所有权、秘密、服务和客户端边界及 Desktop 例外；规范替换不改变产品决定。

### 保留

| 对象 | 去向 |
| --- | --- |
| src 下八个产品项目、tests 下现有测试及夹具 | 保留；不拆项目或重写业务 |
| EF Core 迁移/快照、生成协议、锁文件、资源来源 | 保留生成链；不手工改生成内容 |
| Servitium.sln、global.json、Directory.Build.props、Directory.Packages.props、NuGet.config | 保留生态入口；编译公共设置集中到 props |
| .editorconfig、.gitattributes、.gitignore | 更新为 UTF-8 无 BOM、LF、外部产物与本地配置忽略 |
| legacy 分支、远程配置、历史提交 | 不动；仅在 develop 新增检查点 |

### 移动或改名

| 对象 | 新位置或处理 |
| --- | --- |
| Domain/Common | Domain/Primitives，更新同仓库命名空间引用 |
| Application/Common | Application/Results，更新同仓库引用 |
| Contracts/Common/CommonContracts.cs | Contracts/Transport/TransportContracts.cs，更新引用 |
| PageActionDispatcher.Helpers.cs | PageActionDispatcher.Presentation.cs，仅重命名 |
| Desktop/Resources/<语言>/Resources.resw | Desktop/Strings/<语言>/Resources.resw；生成器同步目标 |
| Client.ContractTests 项目 | Client.Tests，更新解决方案/程序集可见性/命名空间 |
| Directory.Build.targets | 将测试公共编译设置合入 Directory.Build.props，不增加根文件 |
| eng/Build.ps1、Build-JavaFixture.ps1 | tools/build/；保留实际构建能力，路径从忽略的 Local.props 读取 |
| eng/Run.ps1 | tools/run/；只管理本次启动的开发进程 |
| eng/Generate.ps1、Export-OpenApi.ps1 | tools/generate/；保留生成，去除文档门禁/Profile/Verify 检查 |
| eng/Test.ps1、Test-JavaFixture.ps1、coverage.runsettings | tools/test/；使用 xUnit/dotnet test，不保留阶段门禁 |
| 场景验证定义 | validations/<类型>-<主题>/validation.md，写目标、运行、通过标准；结果外置 |

### 移出仓库

移出根为工作区 Artifacts/Servitium；实际绝对路径仅在被忽略的本机配置和外部报告中维护。移前核实对象，不覆盖已有同名内容。

| 对象 | 外部分类 |
| --- | --- |
| artifacts 下 bin/obj/openapi/test-fixtures | Build 或本轮快照 |
| artifacts 下 test-results/test-runs/user-testing | Validation 或 Runtime 的历史快照 |
| artifacts 下 tools/winui-template-reference | Scratches/Servitium 的旧探针/参考工作目录 |
| tests 下 TestResults | Validation 历史 |
| Desktop WebAssetsSource/node_modules | Build/Dependencies；不入库 |
| records 六份流水及逐页审计、旧 AGENTS | Records；原文保存，提取有效结论 |
| requirements-traceability、acceptance-matrix | Records；不保留重复手工映射 |
| 旧 phases、过时工程/工作流/总计划/风险/交付文档 | Records；当前结论转写到新计划/进度/待办/技术债 |
| 外部参考版本、路径及探针流水 | Records；有效协议/格式与研究结论另行整理 |

### 删除

删除指从工作树移出，原文留在上述外部 Records；不永久删除文件。

| 对象 | 理由 |
| --- | --- |
| Validate.ps1、CheckEnvironment.ps1、Check-Docs.ps1、Check-Coverage.ps1、Check-Dependencies.ps1 | T-06，删除自建门禁/检查器 |
| Check-DesktopUi.ps1 | 原脚本外置；真实 UI 验证定义保留，界面重设计由评审另行处理 |
| Clean.ps1 | 禁止永久删除，停止保留旧清理入口 |
| ServitiumTestClient.cs | 仅在确认无使用方后移出死代码；记录具体删除 |
| 空 eng/docs 及迁移产生的空目录 | 移到外部快照；不执行永久删除 |
| CI | 当前没有；本轮不创建 |

### 旧规则去向

| 旧 AGENTS 主题 | 新事实来源 |
| --- | --- |
| 产品/支持/设计/18 决策/13 派生 | project 范围、design 决策与原规格 |
| 净室/外部路径只读/真实样本授权 | documents/AGENTS.md 补充规则；位置存本机配置 |
| 数据所有权/路径/归档/进程/授权/秘密 | 原安全和协议规格保留，补充关键产品不变量 |
| 旧严格串行/Profile/连续三轮/覆盖率门禁 | 退役；测试原则按新规范，未验收事实仍保留 |
| 默认 Git 禁令/Conventional Commits | 本轮明确授权的 Update 单次提交/正常推送替代 |
| 受保护文档审批/阶段报告流水/CI | 新规范及本轮授权替代；不保留自建保护检查 |
| 用户允许先 Desktop、暂停 Web/真样本 | 项目规范和当前进度页 |

### 文档去向

以下是全部 docs 分类的逐类去向；文件名以类型词和下划线主题命名，原始版本均保留外部快照。

| 旧文档 | 新文档或归档 |
| --- | --- |
| index | README 文档入口；外部原文 |
| product/product-definition | project/overview-servitium |
| product/decision-baseline | design/adr-1-product_baseline，保留用户决策正文 |
| product/functional-specification | reference/api-functional_requirements |
| product/hmcl-distribution-specification | design/mechanism-hmcl_distribution |
| product/localization-specification | design/principle-localization |
| architecture/system-architecture | design/architecture-system |
| architecture/protocols-and-contracts | reference/protocol-control_plane |
| architecture/security-model | design/security-threat_model |
| architecture/data-and-storage | design/architecture-storage |
| architecture/repository-and-solution-layout | design/architecture-repository，按当前布局重写 |
| design/information-architecture | design/architecture-information |
| design/shared-ui-specification | design/principle-user_interface |
| design/desktop-specification | design/mechanism-desktop |
| design/web-specification | design/mechanism-web |
| engineering/engineering-standards | development/workflow-development 中当前规程；其余归档 |
| engineering/development-environment | development/setup-development |
| engineering/build-and-validation | development/build-development、testing-development；门禁退役 |
| development/development-workflow | development/workflow-development；旧原文归档 |
| development/master-plan | project/roadmap-development；旧门禁原文归档 |
| development/risk-register | project/debt-runtime、backlog-product；原文归档 |
| development/phases/00～24 | 结论转写 project/phase-1-repository_alignment、进度和 roadmap；过时阶段原文归档，不重新承诺旧 Gate |
| reference/source-evidence-index | reference/upstream-references；原始路径/证据外置 |
| reference/mcsmanager-feature-inventory | research/survey-mcsmanager_features |
| reference/mcsmanager-behavior-matrix | research/survey-mcsmanager_behaviors |
| reference/mcsmanager-frontend-layout | research/survey-mcsmanager_layout |
| reference/hmcl-auto-update-protocol | reference/protocol-hmcl_update |
| reference/hmcl-client-behavior | research/investigation-hmcl_client |
| reference/prism-instance-export-format | reference/format-prism_export |
| reference/local-pack-publishing-prototype | research/survey-pack_publishing |
| testing/test-strategy | development/testing-development |
| testing/backend-acceptance | validations/integration-backend/validation.md |
| testing/frontend-acceptance | validations/ui-clients/validation.md |
| testing/user-testing-procedure | validations/acceptance-product/validation.md |
| testing/acceptance-matrix、requirements-traceability | 重复映射外置；当前结论写 status/debt |
| records/current-status | project/status-development（10 KB 内） |
| records/development-log、validation-log、problem-log | 原文外置；结论写 status/debt/research |
| records/decision-log | 有效决策分别写 design/adr-2 起连续编号；过时/检查器决定外置 |
| records/change-requests | 原文外置；当前未决写 backlog/debt |
| records/desktop-page-gap-audit | 原文外置，PB-039 与当前边界写 debt-desktop |
| delivery/delivery-plan | development/build-packaging 的当前选择与 backlog；旧流水/未来流程外置 |

## 步骤

1. 完成：盘点工作树、规范和旧文档/脚本，创建并逐字核对 Goal。
2. 完成：从微软官方取得 global.json 固定的免安装 SDK，在外部目录记录整理前构建/现有测试。
3. 完成：原样复制新根规范，写项目规范和补充约束，执行目录/文件迁移。
4. 完成：提取当前结论、整理七类文档/验证定义，保持已确认设计依据。
5. 完成：逐节登记存量技术债，低风险清理死代码，格式化并复跑同命令。
6. 完成：自查规则与差异，准备随本页提交的 Update 检查点；正常推送 develop 后汇报并停止。

## 结果

已完成本轮整理。三处风险分别由原生构建/逐项测试比较、源码与产物分流清单、旧规则去向表核对；移动引起的测试夹具定位与工具运行时调用问题已修正。可运行后端测试逐项一致；完整还原/构建/测试保留整理前既有的 NU1903 失败，未改变依赖或测试断言。构建/测试实际结果、外移清单及十项自查保存在仓库外本轮报告；当前剩余产品工作见进度与技术债。
