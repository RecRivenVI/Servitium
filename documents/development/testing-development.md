# 测试与场景验证

保持 xUnit 和现有业务断言，使用原生 dotnet test；不把测试数量或旧覆盖率视为正确性。

## 本地测试

先构建合成 Java 输入和项目，再执行：

```powershell
./tools/build/Build-JavaFixture.ps1
. ./tools/build/Development.ps1
$context = Get-DevelopmentContext
& $context.Dotnet test Servitium.sln --configuration Release --no-restore --maxcpucount:1 --logger trx --results-directory (Join-Path $context.ArtifactsRoot 'Validation/Current')
./tools/test/Test-JavaFixture.ps1 -Category Unit
./tools/test/Test-JavaFixture.ps1 -Category Integration
```

Domain/Application 是领域/用例测试；Infrastructure、Server、Agent 使用独立 SQLite、文件、合成归档/协议或子进程；Client.Tests 处理 HTTP/协议，Desktop.Tests 主要覆盖纯逻辑和契约。架构用例是现有 xUnit 测试，未扩建成仓库规则检查器。

每次改动只跑相关内容，检查点跑受影响集成，交付候选完整跑一次。失败最多原样重跑一次，再做有界调查；不通过更少断言、跳过、放宽超时或吞掉退出码改变结果。

## 真实场景

| 定义 | 范围 |
| --- | --- |
| [后端](../../validations/integration-backend/validation.md) | 服务、ACL、路径、进程、恢复、容量、真实协议 |
| [双客户端](../../validations/ui-clients/validation.md) | 实际 UI、DPI、键盘、辅助功能、跨端一致性 |
| [用户协作](../../validations/acceptance-product/validation.md) | 真实服务端/Prism/HMCL 与短工作流验收 |

这些定义不表示已经执行；独立数据、随机端口与只读参考边界仍有效。运行结果不入库，当前只写一致、有差异或无法比较及原因。
