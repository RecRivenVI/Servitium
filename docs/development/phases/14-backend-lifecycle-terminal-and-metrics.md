# 阶段 14：生命周期、终端与指标

## 1. 阶段目的

让实例真正跑起来：进程启动与停止、Job Object 进程树控制、ConPTY 终端、输出缓冲与扇出、终端 Hub、崩溃识别与自动重启、EULA、服务器查询与 RCON、实例资源指标。

## 2. 需求编号

`REQ-INS-040` … `REQ-INS-043`、`REQ-INS-050`、`REQ-INS-051`、`REQ-INS-060` … `REQ-INS-066`、`REQ-INS-070`、`REQ-INS-071`、`REQ-INS-080`、`REQ-INS-081`、`REQ-TRM-001` … `REQ-TRM-040`、`REQ-NOD-002`（实例维度补充）、`THR-050` … `THR-054`。

## 3. 前置条件

* 阶段 13 完成条件全部满足。

## 4. 范围

* 进程启动器（参数数组、环境变量、工作目录、运行账户）；
* Agent 本地凭据登记、列出与删除命令；
* Windows Job Object 管理；
* ConPTY 与重定向管道两种模式；
* 输出读取、解码、环形缓冲、扇出；
* 终端 Hub 与 gRPC 终端子流；
* 实例状态机的运行时落地；
* 停止序列与强制终止；
* 崩溃识别、自动启动、自动重启；
* EULA 检查与写入；
* Minecraft Server List Ping 与 RCON；
* 实例资源指标；
* 系统关机时的优雅停止。

## 5. 非目标

* 不实现文件管理、配置、模组（阶段 15）；
* 不实现计划任务（阶段 16）；
* 不实现前端终端界面（阶段 21）。

## 6. 需要创建的项目或目录

```text
src/Servitium.Agent/
├── Processes/     ProcessLauncher、JobObjectManager、ConPtySession、ProcessMonitor
├── Credentials/   本地交互式登记命令、DPAPI 存储、别名解析
├── Terminals/     OutputBuffer、TerminalSession、SubscriberFan
├── Lifecycle/     InstanceLifecycleService、StopSequence、AutoRestartPolicy、CrashDetector
├── Protocols/     ServerListPingClient、RconClient
└── Metrics/       InstanceResourceSampler

src/Servitium.Server/
├── Hubs/TerminalHub
└── AgentGateway/TerminalStreamBroker
```

## 7. 需要实现的类型或模块

### 7.1 进程启动

* 参数数组直接传递，**不经 shell**；
* 工作目录为实例工作目录；
* 环境变量：继承 + 实例配置的追加/覆盖；
* 运行账户：未配置时用 Agent 服务账户；配置时按别名从 Agent 本地 DPAPI 存储读取，以 `CreateProcessWithLogonW` 等价方式启动；密码不经过控制平面；
* 启动前校验：Java 运行时存在且可执行、工作目录可访问、EULA 已同意、启动配置完整。

### 7.2 Job Object

* 每实例一个 Job Object；
* `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`；
* `JOB_OBJECT_LIMIT_BREAKAWAY_OK = false` 且 `SILENT_BREAKAWAY = false`；
* 进程创建后立即加入；
* Agent 持有句柄直到实例停止；
* 强制终止 = 关闭句柄。

### 7.3 ConPTY

* 创建伪控制台，初始尺寸取实例配置（默认 164×40）；
* 输入与输出管道；
* `ResizePseudoConsole` 支持尺寸调整；
* 可按实例关闭，关闭时使用标准重定向管道（无 ANSI、无尺寸概念）；
* 进程退出后正确关闭伪控制台与读取任务，不遗留线程。

### 7.4 输出处理

* 按实例配置的输出编码解码字节流；
* 处理跨块的多字节字符（保留不完整序列到下一块）；
* 环形缓冲：默认上限 1 MiB 与 5000 行（先到者），可按节点配置；
* 缓冲被截断时插入可见标记；
* 批量推送：每 50 毫秒或累计 8 KiB 触发。

### 7.5 终端 Hub 与子流

* 客户端 `Attach` → 授权校验 → 若该实例尚无终端子流则打开 → 推送缓冲快照 → 转发实时输出；
* `SendInput` → 授权校验 → 经子流写入 → Agent 按输入编码与换行模式写入进程标准输入；
* `Resize` → 多客户端时取最小公共尺寸；
* `Detach` 或连接断开 → 减少订阅；最后一个订阅者离开后子流保留 60 秒再关闭（避免频繁开关）；
* 背压：每连接发送队列上限 4 MiB，超出时丢弃最旧并插入标记；
* 授权撤销时主动关闭订阅（`REQ-IDN-032`）。

### 7.6 停止序列

```text
1. 状态 → Stopping
2. 向标准输入写入停止命令（默认 stop）
3. 等待 StopTimeoutSeconds（默认 60）
4. 仍在运行 → 发送 CTRL+BREAK 到进程组
5. 再等 5 秒
6. 仍在运行 → 关闭 Job Object
7. 状态 → Stopped
```

每一步转换写入操作日志。

### 7.7 崩溃与自动重启

* 进程退出且不是 Servitium 主动停止触发 → `Crashed`，记录退出码与最近 200 行输出；
* 自动重启：按策略重启，计数递增；达到上限后停止并推送 `instance.auto-restart-exhausted`；
* 成功运行超过阈值（默认 10 分钟）后计数重置；
* 自动启动：Agent 启动后按配置延迟启动。

### 7.8 EULA

* 启动前读取工作目录的 `eula.txt`；
* 不存在或 `eula` 非 `true` 时返回 `eula-not-accepted`；
* 用户确认后 Agent 把 `eula=true` 写回，**保留文件中其余内容与注释**（复用阶段 15 的 properties 保留结构写入器；本阶段先实现最小的行级替换，阶段 15 统一到解析器）。

### 7.9 服务器查询与 RCON

* Server List Ping：实现握手 + 状态请求 + 响应解析（版本、MOTD、在线/最大玩家、玩家样本）；实例运行时每 15 秒查询一次；
* RCON：实现认证、命令、响应组装（含多包响应）、断线重连；密码从 Agent 本地 DPAPI 解密；玩家列表通过 `list` 命令获取并解析。

### 7.10 实例资源指标

CPU 时间差分、工作集、句柄数、线程数；随实例状态一起上报。

### 7.11 系统关机

Agent 服务响应停止请求：对全部运行中实例并行执行停止序列，总时长受 `关机等待秒数` 约束（默认 60 秒），超时后关闭全部 Job Object。

## 8. 数据模型

Agent 本地新增 `LocalInstanceRuntime`（状态、进程标识、启动时间、重启计数、最后退出码）。

控制平面无新增实体；实例镜像增加运行时字段。

## 9. API 或事件

REST：`instances` 的生命周期动作、`command`、`status`、`players`、`rcon/command`、`logs`、`eula/accept`、`update`、`batch/{action}`。

Hub：`/hubs/v1/terminal` 全部方法。

事件：`instance.status-changed`、`instance.resource-usage`、`instance.crashed`、`instance.auto-restart-exhausted`、`instance.players-changed`。

gRPC：`StartInstance`、`StopInstance`、`RestartInstance`、`ForceStopInstance`、`SendCommand`、`AcceptEula`、`GetInstanceStatus`、`QueryPlayers`、`RconCommand`、`RunUpdateAction`；终端子流。

## 10. 实施顺序

1. Job Object 封装 + 测试；
2. 本地凭据登记与 DPAPI 存储、进程启动器（参数数组、环境、工作目录、凭据别名）+ 测试；
3. ConPTY 封装 + 重定向管道回退 + 测试；
4. 输出读取与解码（含跨块多字节）+ 测试；
5. 环形缓冲与截断标记 + 测试；
6. 实例生命周期服务：启动、状态机落地；
7. 停止序列；
8. 强制终止；
9. 崩溃识别；
10. 自动重启与自动启动；
11. EULA 检查与写入；
12. 终端子流（Agent 侧）；
13. 终端子流（控制平面侧）与 `TerminalHub`；
14. 多客户端扇出与背压；
15. 尺寸协商；
16. 会话日志导出；
17. Server List Ping；
18. RCON；
19. 实例资源指标；
20. 系统关机优雅停止；
21. 更新动作（JAR 替换，HMCL 同步留到阶段 17）；
22. 批量生命周期操作；
23. 日志文件读取；
24. 测试；
25. 运行 `eng/Validate.ps1`。

## 11. 关键算法

| 算法 | 说明 |
| --- | --- |
| 跨块多字节解码 | 使用有状态的 `Decoder`，保留不完整序列到下一块；块边界不得产生替换字符 |
| 环形缓冲 | 按字节与行双上限；写入时从头淘汰整行；淘汰发生时置位「已截断」标记，供新订阅者感知 |
| 尺寸协商 | 取全部活跃订阅者尺寸的逐维最小值；无订阅者时保持最后值 |
| 停止序列 | 见 7.6；每步都以「进程是否仍在运行」为条件，不以固定等待为唯一依据 |
| 自动重启计数 | 重启后启动计时器；运行超过阈值则重置计数；未超过则计数累加 |
| Server List Ping | 握手包（协议版本 -1，下一状态 1）→ 状态请求 → 读取 VarInt 长度 + JSON；带超时与重试 |
| RCON 多包响应 | 发送命令后追加一个空的 `RESPONSE_VALUE` 探测包，按其响应标识拼接完整输出 |
| CPU 使用率 | 两次采样的进程内核态 + 用户态时间差除以墙钟时间差与逻辑核数 |

## 12. Windows 平台事项

* Job Object：`CreateJobObject`、`SetInformationJobObject`、`AssignProcessToJobObject`、`SafeJobHandle`；
* ConPTY：`CreatePseudoConsole`、`ResizePseudoConsole`、`ClosePseudoConsole`、`InitializeProcThreadAttributeList`、`UpdateProcThreadAttribute`；
* `CTRL+BREAK`：`GenerateConsoleCtrlEvent` 到进程组，或在无控制台时通过 ConPTY 输入序列；
* 运行账户：`CreateProcessWithLogonW`；
* 进程时间：`GetProcessTimes`；
* 服务关机：`IHostApplicationLifetime` + Windows 服务的停止超时配置。

## 13. 安全要求

* `THR-050` 命令注入：全程参数数组，无 shell；测试用含 shell 元字符的参数验证原样传递；
* `THR-051` 任意可执行文件：原始命令模式的首参数必须是已登记运行时；
* `THR-052` 高级原始命令：全局开关 + 角色限制 + 审计；
* `THR-053` 进程树逃逸：Job Object 配置与两项强制测试；
* `THR-054` 运行账户凭据：仅通过 Agent 本地安全提示登记，以 DPAPI 存储；控制平面与 gRPC 只处理别名，秘密不进入命令行、环境、日志与响应。

## 14. 自动化测试

单元测试：停止序列的状态判定、自动重启计数、尺寸协商、环形缓冲淘汰、CPU 使用率计算、Server List Ping 与 RCON 的协议编解码。

## 15. 集成测试

使用 `Servitium.JavaServerFixture`：

| 编号族 | 内容 |
| --- | --- |
| `INT-500` | 启动 → `Running`；输出可读；停止 → `Stopped` |
| `INT-510` | 不响应停止命令的进程：完整降级序列生效并最终停止 |
| `INT-520` | 强制终止：派生 3 层子进程后整棵树消失 |
| `INT-530` | 杀死 Agent 进程：实例进程树在 5 秒内消失 |
| `INT-540` | 崩溃识别：非 0 退出码 → `Crashed` 且记录退出码 |
| `INT-550` | 自动重启：达到上限后停止并推送事件；成功运行后计数重置 |
| `INT-560` | 自动启动：Agent 重启后按配置与延迟启动 |
| `INT-570` | EULA：未同意时启动被拒；同意后写入且保留原有内容 |
| `INT-580` | ConPTY：ANSI 序列与非 ASCII 输出正确到达；关闭伪终端后回退管道模式可用 |
| `INT-590` | 编码：GBK 输出被正确解码；跨块多字节无替换字符 |
| `INT-600` | 终端多客户端：3 个订阅者同时收到相同输出；观察者数正确 |
| `INT-610` | 终端背压：高速输出下慢客户端收到截断标记而非阻塞其他客户端 |
| `INT-620` | 尺寸协商：多客户端时取最小值 |
| `INT-630` | 断线重连：重连后收到缓冲快照并继续实时流 |
| `INT-640` | 授权撤销：终端订阅在 2 秒内关闭 |
| `INT-650` | Server List Ping：夹具响应被正确解析 |
| `INT-660` | RCON：认证、命令、多包响应、断线重连 |
| `INT-670` | 系统关机：服务停止时实例被优雅停止 |
| `INT-680` | 控制平面停止时实例继续运行；恢复后状态一致 |
| `INT-690` | 批量生命周期操作：部分失败时其余继续 |
| `INT-700` | 日志文件读取：按行范围与关键字 |

安全测试：`SEC-200`、`SEC-210`、`SEC-220` … `SEC-222`、`SEC-230`、`SEC-231`、`SEC-240`。

**平台矩阵**：`INT-580`、`INT-590`、`INT-520`、`INT-530` 必须在 Windows 11 与至少一个 Windows Server 版本上各运行一次（`RSK-001`）。

## 16. 人工测试

无（终端界面在阶段 21 与 24 验证）。

## 17. 调试启动方式

```powershell
dotnet run --project src/Servitium.Server -- --environment Development
dotnet run --project src/Servitium.Agent -- --environment Development
# 通过 OpenAPI 或临时脚本创建实例、绑定 JavaServerFixture、启动
./eng/Test.ps1 -Category Integration
```

## 18. 验收责任方

编码 Agent 自主验收。

## 19. 完成条件

1. 实例可启动、停止、重启、强制终止，状态机正确；
2. 进程树在强制终止与 Agent 崩溃两种情况下都被完整回收；
3. 终端可用：多客户端、编码、ANSI、尺寸、缓冲、背压、重连；
4. 崩溃识别与自动重启策略生效；
5. EULA 流程生效且不破坏文件其余内容；
6. Server List Ping 与 RCON 可用；
7. 控制平面停止时实例继续运行；
8. `INT-500` … `INT-700` 全部通过（含平台矩阵）；
9. 本阶段列出的全部 `SEC-*` 通过；
10. `eng/Validate.ps1` 全量通过。

## 20. 禁止提前进入下一阶段的条件

* 进程树回收测试未通过；
* 终端存在乱码、丢输出或阻塞其他客户端的情况；
* 停止序列未完整实现降级；
* 命令启动经过 shell；
* 控制平面停止会导致实例停止。

## 21. 需要更新的动态记录

* `docs/records/current-status.md`
* `docs/records/development-log.md`
* `docs/records/decision-log.md`（本阶段产生实现级选择时）
* `docs/records/problem-log.md`（记录 ConPTY 与 Job Object 的跨版本差异及其他缺陷，关联 `RSK-001`、`RSK-002`）
* `docs/records/validation-log.md`
* `docs/testing/requirements-traceability.md`
* `docs/testing/acceptance-matrix.md`
