# 阶段 10：后端工程基础

## 1. 阶段目的

建立可构建、可测试、可验证的工程骨架：解决方案、构建配置、目录结构、`eng/` 脚本与测试项目骨架。此后每个阶段都在这套骨架上增量推进。

## 2. 需求编号

本阶段不实现业务需求。间接支撑：`REQ-PLT-001`（时间与标识约定的编译期基础）、`REQ-I18N-004`（本地化校验骨架）。

## 3. 前置条件

* 阶段 00 完成条件全部满足；
* `docs/records/current-status.md` 记录阶段 00 已完成；
* 开发环境满足 `docs/engineering/development-environment.md` 第 1 节。

## 4. 范围

* 解决方案与构建配置文件；
* `src/` 与 `tests/` 目录及全部项目的空骨架（可编译、可运行测试，但无业务实现）；
* `eng/` 脚本（`CheckEnvironment.ps1`、`Build.ps1`、`Test.ps1`、`Clean.ps1`、`Generate.ps1`、`Validate.ps1`、`Run.ps1`）；
* `artifacts/` 输出重定向；
* 架构测试的基础设施与第一批断言；
* 测试夹具项目 `Servitium.JavaServerFixture`；
* 文档一致性检查脚本（从阶段 00 的临时脚本转为 `eng/` 中的永久脚本）。

## 5. 非目标

* 不实现任何领域模型、用例或端点；
* 不创建数据库迁移；
* 不创建 `src/Servitium.Web/`；
* 不创建 `src/Servitium.Desktop/`；
* 不创建上述两个项目对应的测试项目；
* 上述项目只在阶段 20–22 按对应阶段文档创建；
* 不实现 `Servitium.Client` 的正式客户端能力（本阶段只创建可编译的空骨架，正式客户端能力在阶段 20 实现）；
* 不创建 `installer/`；
* 不创建 `.github/workflows/`。

## 6. 需要创建的项目或目录

```text
Servitium.sln
Directory.Build.props
Directory.Packages.props
NuGet.config
global.json
.editorconfig
.gitattributes
.gitignore

src/Servitium.Domain/
src/Servitium.Application/
src/Servitium.Contracts/
src/Servitium.Infrastructure/
src/Servitium.Client/
src/Servitium.Server/
src/Servitium.Agent/

tests/Servitium.Domain.Tests/
tests/Servitium.Application.Tests/
tests/Servitium.Infrastructure.IntegrationTests/
tests/Servitium.Server.IntegrationTests/
tests/Servitium.Agent.IntegrationTests/
tests/Servitium.Client.ContractTests/
tests/Fixtures/Servitium.JavaServerFixture/

eng/
eng/modules/
```

`src/Servitium.Desktop/`、`src/Servitium.Web/` 与其测试项目在阶段 20–22 创建。

命名必须符合 `docs/architecture/repository-and-solution-layout.md` 第 2 节。

## 7. 需要实现的类型或模块

| 位置 | 内容 |
| --- | --- |
| `Servitium.Domain` | `AssemblyInfo` 与命名空间骨架；无类型 |
| `Servitium.Application` | 同上 |
| `Servitium.Contracts` | 稳定错误码常量类 `ErrorCodes`（按 `protocols-and-contracts.md` 2.5 节全量登记）；`Protos/` 目录（空 `.proto` 骨架，`package servitium.agent.v1`） |
| `Servitium.Infrastructure` | 空骨架 |
| `Servitium.Client` | 空骨架 |
| `Servitium.Server` | 最小 `Program.cs`：启动、`/health/live`、`/health/ready`、结构化日志、选项绑定与启动校验骨架 |
| `Servitium.Agent` | 最小 `Program.cs`：Windows 服务宿主骨架、日志、PID 输出 |
| `Servitium.Domain.Tests` | 架构测试基础设施 + 第一批断言（见第 14 节） |
| `Servitium.JavaServerFixture` | 可控 Java 测试服务端（见 `repository-and-solution-layout.md` 4.1） |

## 8. 数据模型

无。

## 9. API 或事件

仅健康检查：

| 端点 | 说明 |
| --- | --- |
| `GET /health/live` | 进程存活 |
| `GET /health/ready` | 依赖就绪（本阶段恒为就绪） |

## 10. 实施顺序

1. 创建 `global.json`、`Directory.Build.props`、`Directory.Packages.props`、`NuGet.config`、`.editorconfig`、`.gitattributes`、`.gitignore`；
2. 创建 `Servitium.sln` 与 `src/` 下七个项目；
3. 配置 `artifacts/` 输出重定向；
4. 创建 `tests/` 下六个测试项目与夹具项目；
5. 在 `Servitium.Contracts` 中登记全部稳定错误码常量；
6. 实现 `Servitium.Server` 的最小宿主与健康检查；
7. 实现 `Servitium.Agent` 的最小服务宿主；
8. 实现 `Servitium.JavaServerFixture`；
9. 编写架构测试基础设施与第一批断言；
10. 创建 `eng/CheckEnvironment.ps1`；
11. 创建 `eng/Build.ps1`、`Test.ps1`、`Clean.ps1`；
12. 创建 `eng/Generate.ps1`（本阶段仅处理 protobuf 与错误码一致性）；
13. 创建 `eng/Validate.ps1`，登记阶段 1–6、9–11、17–18、21；未到所属开发阶段的检查不预先登记，也不输出占位状态；
14. 把文档一致性检查从临时脚本转为 `eng/` 永久脚本并接入 `Validate.ps1`；
15. 创建 `eng/Run.ps1`；
16. 运行 `eng/Validate.ps1` 全量并确认通过。

## 11. 关键算法

无。

## 12. Windows 平台事项

* `Servitium.Agent` 使用 Windows 服务宿主（`AddWindowsService`），同时支持以控制台方式运行以便调试；
* 应用清单启用长路径感知（`longPathAware`）；
* `.ps1` 使用 CRLF 行尾；
* `eng/` 脚本使用 PowerShell 7 语法。

## 13. 安全要求

* `.gitignore` 必须排除本地设置、密钥文件、`artifacts/`、`node_modules/`；
* `NuGet.config` 启用包源映射；
* 启用 `RestorePackagesWithLockFile`，`Validate.ps1` 使用 `--locked-mode`；
* 依赖漏洞扫描接入 `Validate.ps1`。

## 14. 自动化测试

架构测试（分类 `Architecture`）第一批断言：

| 编号 | 断言 |
| --- | --- |
| `ARC-001` | `Servitium.Domain` 不引用任何其他 Servitium 项目 |
| `ARC-002` | `Servitium.Domain` 不引用 EF Core、ASP.NET Core、`System.IO` 以外的 I/O 库 |
| `ARC-003` | `Servitium.Application` 只引用 `Domain` 与 `Contracts` |
| `ARC-004` | `Servitium.Contracts` 不引用任何其他 Servitium 项目 |
| `ARC-005` | `Servitium.Client` 不引用 `Domain`、`Application`、`Infrastructure` |
| `ARC-006` | 项目引用图与 `system-architecture.md` 3.1 节一致 |
| `ARC-010` | `Contracts.ErrorCodes` 中的常量集合与 `protocols-and-contracts.md` 2.5 节的表格一致（通过随源码维护的清单文件比对） |

单元测试（分类 `Unit`）：

| 编号 | 断言 |
| --- | --- |
| `UNT-001` | `JavaServerFixture` 的参数解析正确 |

## 15. 集成测试

| 编号 | 内容 |
| --- | --- |
| `INT-001` | `Servitium.Server` 可启动并响应 `/health/live` 与 `/health/ready` |
| `INT-002` | `Servitium.Agent` 可以控制台模式启动并输出 PID 行后正常退出 |
| `INT-003` | `JavaServerFixture` 可被启动、产生输出、按参数崩溃或响应停止命令 |

## 16. 人工测试

无。

## 17. 调试启动方式

```powershell
dotnet run --project src/Servitium.Server -- --environment Development
dotnet run --project src/Servitium.Agent -- --environment Development
./eng/Validate.ps1
```

## 18. 验收责任方

编码 Agent 自主验收。

## 19. 完成条件

1. `eng/Validate.ps1` 全量运行通过，退出码 0；
2. 阶段列表中无「跳过」状态；
3. 全部项目在 Release 配置下无警告构建；
4. `dotnet format --verify-no-changes` 通过；
5. 架构测试 `ARC-001` … `ARC-010` 全部通过；
6. 集成测试 `INT-001` … `INT-003` 全部通过；
7. 依赖锁定文件已生成且 `--locked-mode` 还原成功；
8. 依赖漏洞扫描无高危项；
9. 文档一致性检查脚本已迁入 `eng/` 并在 `Validate.ps1` 中执行通过；
10. 目录与项目命名符合 `repository-and-solution-layout.md` 第 2 节；
11. 未创建 `installer/` 与 `.github/workflows/`。

## 20. 禁止提前进入下一阶段的条件

* `Validate.ps1` 未通过；
* 存在编译警告；
* 架构测试未建立或未通过；
* `artifacts/` 输出重定向未生效（构建产物散落在项目目录）。

## 21. 需要更新的动态记录

* `docs/records/current-status.md`
* `docs/records/development-log.md`
* `docs/records/decision-log.md`（记录选定的测试框架、断言库、日志库、架构测试库等实现级选择）
* `docs/records/validation-log.md`
* `docs/testing/requirements-traceability.md`：在既有逐需求条目中填写本阶段产生的实现位置、测试与状态
* `docs/testing/acceptance-matrix.md`：在既有验收项中填写本阶段产生的状态与证据
