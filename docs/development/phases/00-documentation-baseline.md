# 阶段 00：文档基线

## 1. 阶段目的

建立 Servitium 的完整文档基线：产品规格、参考证据快照、架构规格、界面规格、工程规范、开发计划、测试计划、动态记录框架、交付计划与 Agent 治理规则，使后续编码 Agent 能够从明确基线开始开发，并在实现需要时继续核实参考证据。

## 2. 需求编号

本阶段不实现需求，而是定义全部需求（`REQ-*`，共 262 条，见 `docs/product/functional-specification.md`）。

## 3. 前置条件

* 仓库位于 `develop` 分支；
* `develop` 不继承旧产品代码；
* 可访问 `D:\GitHubPublic\MCSManager`、`D:\GitHubPublic\HMCL`、`D:\GitHubRecRivenVI\PrismLauncher` 与 `E:\Minecraft\MinecraftServersDuoyun\test`。

## 4. 范围

* 建立 MCSManager 功能、行为链与逐页界面结构的初始证据快照；
* 建立 HMCL 服务端整合内容自动更新协议的源码证据与运行证据；
* 建立 Prism Launcher 原生实例 ZIP 导出结构与内容的源码证据；
* 建立本地整合包发布原型的候选、Release、Channel、Prism 转换与双格式派生证据；
* 产品定义、决策基线、功能规格、HMCL 分发规格、本地化规格；
* 系统架构、数据与存储、协议与接口、安全模型、仓库布局；
* 信息架构与页面规格、共享界面规格、Desktop 与 Web 规格；
* 工程规范、开发环境、构建与验证；
* 开发工作流、总体计划、风险登记、15 份阶段文档；
* 测试策略、后端验收、前端验收、用户测试流程、验收矩阵、追溯矩阵；
* 动态记录文件的初始框架；
* 交付计划；
* `README.md` 与 `AGENTS.md`。

## 5. 非目标

* 不编写任何产品代码；
* 不创建 `.NET` 项目、React 项目、数据库迁移、安装器或 CI 工作流；
* 不创建 `src/`、`tests/`、`eng/`、`installer/`、`artifacts/` 目录；
* 不创建空文件或 `.gitkeep`；
* 不执行 `git add`、`git commit`、`git push`、打标签或发布。

## 6. 需要创建的项目或目录

仅：

```text
README.md
AGENTS.md
docs/index.md
docs/product/          5 个文件
docs/reference/        8 个文件
docs/architecture/     5 个文件
docs/design/           4 个文件
docs/engineering/      3 个文件
docs/development/      3 个文件 + phases/ 15 个文件
docs/testing/          6 个文件
docs/records/          6 个文件
docs/delivery/         1 个文件
```

## 7. 需要实现的类型或模块

无。

## 8. 数据模型

无。文档中定义的数据模型在阶段 11–17 实现。

## 9. API 或事件

无。文档中定义的接口在阶段 12–17 实现。

## 10. 实施顺序

1. 核查仓库与分支状态；
2. 记录三个 Git 参考仓库的当前分支、提交与工作树状态，并记录本地发布原型的证据文件清单哈希，形成可复查的版本定位；
3. 建立 MCSManager 源码证据（路由、布局、组件、面板路由、节点事件、服务、数据模型、权限、审计）；
4. 建立 HMCL 源码证据（manifest、更新算法、HTTP 行为、加载器、入口流程）；
5. 执行 HMCL 初始运行验证（本地 HTTP 服务 + 驱动 HMCL 自身的更新任务，观察真实请求与文件效果）；
6. 撰写 `docs/reference/` 八份文档；
7. 撰写 `docs/product/` 五份文档（含全部需求编号）；
8. 撰写 `docs/architecture/` 五份文档；
9. 撰写 `docs/design/` 四份文档；
10. 撰写 `docs/engineering/` 三份文档；
11. 撰写 `docs/development/` 三份文档与 15 份阶段文档；
12. 撰写 `docs/testing/` 六份文档；
13. 撰写 `docs/records/` 六份初始文件；
14. 撰写 `docs/delivery/delivery-plan.md`；
15. 撰写 `docs/index.md`、`README.md`、`AGENTS.md`；
16. 执行文档一致性检查（链接、空文件、编号、命名、占位词、覆盖）；
17. 若存在旧的 `DEVELOPMENT_PLAN.md`，在全部新文档完成且一致性验证通过后删除；
18. 核对本阶段完成条件。

## 11. 关键算法

无。本阶段的关键产出是 HMCL 更新算法的**规范化描述**（见 `docs/reference/hmcl-auto-update-protocol.md` 第 5 节），供阶段 17 直接实现。

## 12. Windows 平台事项

初始取证与文档编写在 Windows 上进行。运行验证使用本机 JDK 编译 HMCL 核心模块并驱动其任务，不修改参考仓库。

## 13. 安全要求

* 净室原则：不复制参考项目的源码、资源、样式、图标、文案或品牌元素；
* 四个参考路径保持只读，不新增文件、不修改源码或原型文件；
* 临时脚本与编译产物置于会话临时目录，不进入仓库。

## 14. 自动化测试

无自动化测试。使用临时脚本执行文档一致性检查（临时脚本不保留）。

## 15. 集成测试

HMCL 协议运行验证属于本阶段的验证工作，结果记录在 `docs/reference/hmcl-auto-update-protocol.md` 与 `hmcl-client-behavior.md` 中并标注 `[RUNTIME]`。

## 16. 人工测试

无。

## 17. 调试启动方式

无。

## 18. 验收责任方

本阶段由执行 Agent 自主验收，并在最终回复中向用户报告证据范围、基线决定与产物。

## 19. 完成条件

1. 三个 Git 参考仓库的初始分支、提交与工作树状态，以及本地发布原型的证据文件清单哈希均已记录；
2. MCSManager 在登记快照上的非厂商功能已形成清单并分类（采用 / 适配 / 排除 / 不适用）；
3. MCSManager 在登记快照上的路由、页面与节点行为已分类；
4. MCSManager 在登记快照上的前端页面已形成逐页结构证据；
5. HMCL 自动更新在登记快照上的源码调用链已形成证据，manifest、路径、哈希、加载器与更新算法已确定；
6. HMCL 客户端的真实要求与 Servitium 的扩展能力已区分；
7. 四个参考路径的初始证据足以支持阶段 10 启动，且不限制后续编码 Agent 按任务需要实地考察；
8. 正式产品文档中无「类似」「参考」「待研究」等模糊要求；
9. 全部关键需求有稳定编号；
10. 需求、功能矩阵、路由、接口、阶段与验收相互对应；
11. `docs/` 命名符合小写与 `kebab-case` 约定；
12. 文档中的未来 `.NET` 项目与 C# 目录符合 `PascalCase`；
13. 未创建源码、测试、安装器或构建目录；
14. 无空文件，无 `.gitkeep`；
15. `README.md` 不含动态状态；
16. `AGENTS.md` 不含需要持续修改的进度；
17. 交付与开发已分离；
18. 旧的 `DEVELOPMENT_PLAN.md` 若存在则已删除；
19. 暂存区为空，无提交，无推送。

## 20. 禁止提前进入下一阶段的条件

* 任一完成条件未满足；
* 初始证据与受保护规范之间存在会影响阶段 10 的未决冲突；
* 文档一致性检查未通过。

## 21. 需要更新的动态记录

* `docs/records/current-status.md`：记录阶段 00 完成、阶段 10 为下一阶段；
* `docs/records/development-log.md`：记录本阶段的初始证据范围与产物；
* `docs/records/validation-log.md`：记录 HMCL 运行验证与文档一致性检查的结果。
