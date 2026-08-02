# 阶段 13：Agent、节点、Java 与实例

## 1. 阶段目的

打通控制平面与节点：Agent 主动连接、注册与凭据、协议协商、心跳、对账；在此之上实现 Java 运行时管理与实例登记（创建、修改、删除登记、删除工作目录）。

## 2. 需求编号

`REQ-NOD-001` … `REQ-NOD-042`、`REQ-JRE-001` … `REQ-JRE-041`、`REQ-INS-001` … `REQ-INS-030`、`REQ-PLT-040`、`REQ-SEC-050`（Java 导入与归档创建实例部分）、`REQ-SEC-070`（节点侧）、`THR-070` … `THR-073`。

## 3. 前置条件

* 阶段 12 完成条件全部满足。

## 4. 范围

* `Servitium.Agent` 的完整骨架：控制平面连接、指令分发、事件上报、本地存储；
* 控制平面侧的 Agent 网关：连接管理、指令分发、子流复用、对账；
* 节点注册令牌、凭据签发与轮换与吊销；
* Java 运行时：探测、手工登记、导入、验证、删除；
* 实例：创建（三种方式）、修改、删除登记、删除工作目录、批量删除登记；
* 节点指标上报与存储。

## 5. 非目标

* 不实现实例的启动/停止/终端（阶段 14）；
* 不实现文件管理、配置、模组（阶段 15）；
* 不实现计划任务（阶段 16）；
* 不实现 HMCL（阶段 17）。

## 6. 需要创建的项目或目录

```text
src/Servitium.Agent/
├── ControlPlane/     gRPC 客户端、握手、重连、指令分发、事件上报、对账
├── Storage/          Agent 本地 SQLite、迁移、仓储
├── Java/             探测、验证、导入、登记
├── Instances/        实例注册表、创建、修改、删除
├── Metrics/          采样与上报
├── Paths/            Windows 路径校验（Agent 侧权威实现）
├── Archives/         归档安全检查与解包
├── Interop/          注册表、卷信息、文件属性
└── Hosting/          Windows 服务宿主、配置、关机处理

src/Servitium.Server/AgentGateway/
├── ConnectionManager  Agent 会话注册与查找
├── CommandDispatcher  指令下发与超时
├── StreamMultiplexer  子流管理与背压
├── EventIngestor      Agent 事件入库与转发
└── Reconciler         重连对账
```

## 7. 需要实现的类型或模块

### 7.1 Agent 连接

* 启动时读取配置：控制平面地址、凭据（或注册令牌）、TLS 选项（含可选指纹固定）；
* 建立 gRPC 双向流并发送 `Hello`；
* 收到 `Welcome` 后进入工作状态；
* 心跳 10 秒；35 秒无 `HeartbeatAck` 主动重连；
* 断开后指数退避重连（1、2、4、8、16、30 秒封顶）；
* 重连后响应 `Reconcile`。

### 7.2 控制平面 Agent 网关

* 每个 Agent 连接注册为一个会话，按节点标识索引；
* `IAgentGateway` 的实现：按节点分发指令、等待响应、超时；
* 节点离线时指令返回 `node-offline`；
* 协议主版本不一致返回 `node-incompatible` 并关闭；
* 子流复用与背压窗口（本阶段实现框架，终端与文件流在阶段 14–15 使用）。

### 7.3 注册与凭据

* `POST /api/v1/nodes/registration-tokens` 生成令牌（哈希存储、有效期、可选预期名）；
* Agent 首次连接时用令牌换取长期凭据，令牌立即作废；
* 凭据轮换：控制平面签发新凭据 → Agent 确认写入 → 旧凭据失效；
* 吊销：立即断开且拒绝重连。

### 7.4 Java 运行时

| 能力 | 实现 |
| --- | --- |
| 探测 | 扫描常见目录、注册表（含 WOW6432Node）、`JAVA_HOME`、`PATH`；按解析后的真实路径去重 |
| 验证 | 执行 `java -XshowSettings:properties -version`，解析 `java.version`、`java.vendor`、`java.vm.name`、`os.arch`；拒绝 32 位 |
| 手工登记 | 直接给出 `java.exe` 路径后走同样的验证 |
| 导入 | 上传压缩包 → 归档安全检查 → 解包到 Java 数据目录 → 定位 `bin\java.exe` → 验证 |
| 删除 | 取消登记；仅导入型允许删除文件；被使用时先展示占用实例 |

### 7.5 实例登记

| 能力 | 实现 |
| --- | --- |
| 新建空实例 | 在基路径下按标识建目录；`OwnedByServitium = true` |
| 上传归档创建 | 分片上传到 Agent 暂存区 → 归档安全检查 → 解包 → `OwnedByServitium = true`；失败回滚目录 |
| 附加已有目录 | Agent 校验：存在、是目录、可读写、非系统关键目录、非其他实例目录或其子目录、无重解析点；`OwnedByServitium = false` |
| 修改 | 名称、标签、备注、Java 绑定、启动配置、停止配置、终端设置、RCON、查询设置、工作目录（停止时） |
| 删除登记 | 移除记录、授权、（后续阶段的）计划任务与 HMCL 关联；**不触碰文件系统** |
| 删除工作目录 | 仅 `OwnedByServitium = true`；实例必须停止；Agent 重新校验；逐条枚举删除，遇重解析点中止 |

### 7.6 路径校验（Agent 侧权威实现）

实现 `security-model.md` THR-030 的完整九步校验链，供本阶段与阶段 15 共用。

### 7.7 归档安全检查

实现 `REQ-SEC-050` 的全部限制，供 Java 导入、归档创建实例与阶段 15 的解压共用。

### 7.8 指标

CPU、物理内存、卷剩余空间、运行时长；默认 5 秒采样；批量上报；控制平面保留 24 小时。

## 8. 数据模型

控制平面新增：`Node`、`NodeCredential`、`NodeRegistrationToken`、`NodeMetricSample`、`Instance`（镜像）、`JavaRuntime`（镜像）、`InstanceGrant`（阶段 12 已建则补齐外键）。

Agent 本地新增：`LocalInstance`、`LocalJavaRuntime`、`LocalOutboxEvent`。

## 9. API 或事件

REST：`nodes`、`java-runtimes`、`instances`（除生命周期动作外）；见 `protocols-and-contracts.md` 3.3–3.5。

事件：`node.status-changed`、`node.metrics`、`node.credential-rotated`、`instance.created`、`instance.updated`、`instance.unregistered`。

gRPC 指令：`Reconcile`、`UpdateNodeConfig`、`GetMetrics`、`RotateCredential`、`DiscoverJava`、`RegisterJava`、`ImportJava`、`UnregisterJava`、`CreateInstance`、`UpdateInstance`、`UnregisterInstance`、`DeleteWorkingDirectory`。

## 10. 实施顺序

1. Agent 本地存储（SQLite、迁移、仓储）；
2. protobuf 服务的服务端与客户端封装；
3. 控制平面 Agent 网关：连接管理、握手、心跳、协议协商；
4. Agent 侧连接、重连、心跳；
5. 注册令牌与凭据签发；
6. 凭据轮换与吊销；
7. 指令分发与超时；
8. 事件上报与入库；
9. 对账（`Reconcile`）；
10. Agent 侧路径校验实现 + 单元与集成测试；
11. Agent 侧归档安全检查实现 + 测试；
12. Java 探测、验证、手工登记；
13. Java 导入（配合上传会话的最小实现）；
14. Java 删除；
15. 实例创建（三种方式）；
16. 实例修改；
17. 删除登记；
18. 删除工作目录；
19. 批量删除登记；
20. 指标采样与上报；
21. `nodes`、`java-runtimes`、`instances` 端点；
22. 测试；
23. 运行 `eng/Validate.ps1`。

## 11. 关键算法

| 算法 | 说明 |
| --- | --- |
| 路径校验链 | 解码 → 空与控制字符 → 绝对路径 → UNC → 设备路径 → ADS → 归一化与 `..` → 逐级真实路径与重解析点 → 根包含性。任一步失败返回对应错误码 |
| 重解析点检测 | 逐级读取文件属性，检查 `FILE_ATTRIBUTE_REPARSE_POINT`；对最终目标再做一次真实路径解析并比对 |
| 归档安全检查 | 逐条目：路径校验 → 链接条目拒绝 → 累计条目数 → 累计解压大小 → 单条目压缩比 → 目录深度 |
| Java 探测去重 | 对每个候选路径解析真实路径（消除符号链接与 8.3 短名），按真实路径去重 |
| 目录删除 | 深度优先枚举；每一级检查重解析点；先删文件再删目录；遇到拒绝访问时收集错误并在结束时统一报告，不静默忽略 |
| 对账 | Agent 上报本地实例、Java、（阶段 16 起）计划任务的完整快照与版本号；控制平面以 Agent 为准更新镜像；差异写入操作日志 |
| 重连退避 | 1、2、4、8、16、30 秒封顶；连接成功后重置 |

## 12. Windows 平台事项

* 注册表读取：`HKLM\SOFTWARE\JavaSoft\*`、`Eclipse Adoptium`、`BellSoft`，含 `WOW6432Node`；
* 卷信息与剩余空间：`GetDiskFreeSpaceEx`；
* 文件属性与重解析点：`GetFileAttributes`、`FindFirstFile` 的 `dwReserved0`；
* 真实路径解析：`GetFinalPathNameByHandle`；
* 长路径：内部使用 `\\?\` 前缀，不接受用户输入该前缀；
* 系统关键目录判定：`%SystemRoot%`、`%ProgramFiles%`、`%ProgramFiles(x86)%`、`%ProgramData%`（Servitium 自身数据目录除外）、驱动器根。

## 13. 安全要求

必须实现并测试：`THR-070`（伪造 Agent 接入）、`THR-071`（中间人）、`THR-072`（凭据泄露后的持续访问）、`THR-073`（恶意控制平面指令）、`THR-030`（路径穿越）、`THR-031`（文件名）、`THR-032` … `THR-034`（归档）。

Agent **不信任**控制平面传来的路径：每条涉及路径的指令在 Agent 侧独立重新校验。

## 14. 自动化测试

单元测试：路径校验的九类拒绝、文件名合法性、归档限制计算、Java 版本字符串解析、探测去重、重连退避。

架构测试新增：

| 编号 | 断言 |
| --- | --- |
| `ARC-040` | Agent 中所有涉及路径的指令处理都调用路径校验服务 |
| `ARC-041` | Agent 中不存在直接使用 `System.IO` 的绝对路径拼接（必须经路径服务） |

## 15. 集成测试

`Servitium.Agent.IntegrationTests` 与 `Servitium.Server.IntegrationTests`：

| 编号族 | 内容 |
| --- | --- |
| `INT-300` | 注册令牌：正常注册、过期、重复使用、已作废 |
| `INT-310` | 凭据：签发、轮换（旧失效）、吊销（断开且拒绝重连） |
| `INT-320` | 协议协商：主版本不一致被拒并给出原因；次版本不一致降级 |
| `INT-330` | 心跳与重连：断网后按退避重连；恢复后对账 |
| `INT-340` | 对账：Agent 侧有而控制平面无的实例被补齐；反之被清理并记录 |
| `INT-350` | Java 探测：预置多个假 Java 目录与注册表项，断言全部发现且去重 |
| `INT-360` | Java 验证：真实 Java 通过；伪造可执行文件被拒；32 位被拒 |
| `INT-370` | Java 导入：正常导入、缺 `java.exe`、恶意归档被拒 |
| `INT-380` | Java 删除：占用时提示；导入型可删文件；探测型无删文件路径 |
| `INT-390` | 实例创建三种方式；失败回滚 |
| `INT-400` | 附加目录的全部拒绝条件 |
| `INT-410` | 删除登记不触碰文件系统（断言目录仍存在） |
| `INT-420` | 删除工作目录：受管目录成功；外部目录无入口；基路径本身被拒；含目录联接被中止 |
| `INT-430` | 批量删除登记：部分失败时其余继续 |
| `INT-440` | 指标上报与 24 小时保留 |
| `INT-450` | 节点移除：有实例时阻断；确认后级联删除登记 |

安全测试：`SEC-140`（九类路径拒绝）、`SEC-150`、`SEC-160` … `SEC-164`、`SEC-290`、`SEC-291`、`SEC-300`、`SEC-301`、`SEC-310`、`SEC-320`。

## 16. 人工测试

无。

## 17. 调试启动方式

```powershell
# 终端 1
dotnet run --project src/Servitium.Server -- --environment Development
# 浏览器完成初始化，在节点页生成注册令牌

# 终端 2
dotnet run --project src/Servitium.Agent -- --environment Development `
  --server https://127.0.0.1:5344 --register-token <令牌>
```

## 18. 验收责任方

编码 Agent 自主验收。

## 19. 完成条件

1. Agent 可注册、连接、心跳、断线重连、对账；
2. 凭据可轮换与吊销且立即生效；
3. 协议版本协商生效；
4. Java 探测在预置环境中全部发现且去重；
5. 三种实例创建方式可用且失败可回滚；
6. 删除登记不触碰文件系统；删除工作目录的全部约束生效；
7. `INT-300` … `INT-450` 全部通过；
8. 本阶段列出的全部 `SEC-*` 通过；
9. `ARC-040`、`ARC-041` 通过；
10. `eng/Validate.ps1` 全量通过。

## 20. 禁止提前进入下一阶段的条件

* Agent 未实现独立的路径校验（依赖控制平面校验）；
* 外部工作目录存在任何递归删除路径；
* 归档安全检查缺项；
* 凭据轮换或吊销不生效；
* 对账未实现。

## 21. 需要更新的动态记录

* `docs/records/current-status.md`
* `docs/records/development-log.md`
* `docs/records/decision-log.md`（本阶段产生实现级选择时）
* `docs/records/problem-log.md`（记录 Windows 互操作问题与其他缺陷）
* `docs/records/validation-log.md`
* `docs/testing/requirements-traceability.md`
* `docs/testing/acceptance-matrix.md`
