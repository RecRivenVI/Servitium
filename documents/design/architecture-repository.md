# 仓库结构

当前为八个产品项目、七个 xUnit 测试项目和两个合成夹具项目；保留部署与真实复用边界，本轮没有重新拆层。

| 位置 | 职责 |
| --- | --- |
| src/Servitium.Domain | 领域不变量，Primitives 放领域基础类型 |
| src/Servitium.Application | 用例/授权/端口，Results 放用例结果与游标模型 |
| src/Servitium.Contracts | 协议 DTO/protobuf/UI/双语来源，Transport 放传输信封 |
| src/Servitium.Infrastructure | 存储/加密/出站/内容库等端口实现 |
| src/Servitium.Server、Servitium.Agent | 控制平面与 Windows 节点执行部署单元 |
| src/Servitium.Client、Servitium.Desktop | 协议客户端与原生交互部署单元 |
| tests | xUnit；真实 I/O/进程项目使用 IntegrationTests 后缀，Client.Tests 为协议单元/主机测试 |
| tools/build、generate、run、test | 构建、生成、隔离运行和测试辅助，无门禁 |
| validations | 可重复真实场景定义，输入/结果外置 |
| documents | 七种分类与项目规范，当前状态单独维护 |

未来 Web 使用根 web/，当前未创建。安装器、实验、结果与历史记录不在当前源码树中。依赖方向见[系统架构](architecture-system.md)，现存分层过度、巨型文件与不符合单主要类型的文件见[技术债](../project/debt-rules.md)。
