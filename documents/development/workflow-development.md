# 开发工作流

从仓库根目录运行技术栈命令；没有 Profile、文档保护检查器、覆盖率门禁或 CI。

## 日常步骤

1. 阅读根规范、项目规范和[进度](../project/status-development.md)，确认工作树与范围。
2. 多步工作先写计划；不要把本机路径、凭证或结果入库。
3. 按影响运行构建、xUnit 与格式化；已有失败如实保留，不降低断言或忽略高危依赖。
4. 更新对应设计/配置/依赖与短进度，说明一致、有差异或无法比较。
5. 只有明确授权才提交/推送；标题 Update，无正文。

## 构建、测试和格式

```powershell
./tools/build/Build.ps1
./tools/test/Test.ps1
./tools/test/Test.ps1 -Project tests/Servitium.Agent.IntegrationTests/Servitium.Agent.IntegrationTests.csproj
. ./tools/build/Development.ps1
$context = Get-DevelopmentContext
& $context.Dotnet format Servitium.sln --no-restore
```

直接的标准入口及结果比较方式见[构建](build-development.md)和[测试](testing-development.md)。构建脚本先生成合成 Java 输入，再还原和构建；不是额外门禁。

## 开发运行

```powershell
./tools/run/Run.ps1 -Component Server
./tools/run/Run.ps1 -Component Agent
# Desktop 仅在用户明确要求当前界面调试时手动运行：
./tools/run/Run.ps1 -Component Desktop
```

工具打印该次进程 PID、外部数据和日志位置，只管理自己启动的进程。首次 Agent 注册须在隔离 Server 创建一次性令牌并按产品流程登记；不复用用户正式实例。四项真实样本与 Web 仍暂停，当前 UI 不作为验收完成。

## 生成

```powershell
./tools/generate/Generate.ps1
```

它从 Contracts 的双语来源生成 Strings 资源，并从隔离 Server 导出的 OpenAPI 生成 Client 协议清单；不检查文档保护或阶段状态。生成物不手改，protobuf 随构建生成。
