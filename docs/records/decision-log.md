# 决策日志

本文件是**可持续更新**的动态记录，记录**实现级**决策。

## 1. 适用范围

| 属于本文件 | 不属于本文件 |
| --- | --- |
| 规格未约束的实现细节：库选型、内部数据结构、算法实现方式、目录内部组织、测试替身策略 | 产品范围、功能取舍、协议约定、界面结构、阶段顺序、安全要求 |

触及后者的变更**必须**走 `change-requests.md` 流程，不得记在本文件里当作既成事实。

固定的产品与技术决策见 `docs/product/decision-baseline.md`（受保护，不可自行修改）。

## 2. 记录格式

```text
## DL-### <标题>

| 项 | 内容 |
| --- | --- |
| 日期 | YYYY-MM-DD |
| 阶段 | 阶段编号 |
| 决策 | 一句话结论 |
| 背景 | 为什么需要做这个决策 |
| 备选 | 考虑过的其他方案 |
| 理由 | 为什么选这个 |
| 影响 | 涉及哪些项目、文件、需求 |
| 可逆性 | 高 / 中 / 低；改起来的代价 |
| 关联 | 需求编号、风险编号、问题编号 |
```

编号 `DL-###` 连续递增，一经分配不复用。

## 3. 待决策清单

下列决策在文档基线阶段被有意留给实现阶段，因为它们依赖实际的库可用性与验证结果。做出决策时在本文件中登记。

| 编号 | 待决策 | 预计阶段 | 约束 |
| --- | --- | --- | --- |
| P-01 | 测试框架、断言库、替身库 | 10（见 DL-001） | 需支持分类标记与并行控制 |
| P-02 | 架构测试库 | 10（见 DL-002） | 需能断言项目引用图与类型约束 |
| P-03 | 日志库与结构化日志格式 | 10（见 DL-003） | 需支持命名占位符与脱敏钩子 |
| P-04 | Argon2id 实现库 | 12（见 DL-007） | 需支持参数编码进哈希串 |
| P-05 | TOTP 实现库或自实现 | 12（见 DL-007） | RFC 6238，SHA-1，可配置漂移 |
| P-06 | `properties` 保留结构解析：选库或自实现 | 15（见 DL-010） | 必须满足 `REQ-CFG-020` 的字节级往返 |
| P-07 | YAML 保留结构解析：选库或自实现 | 15 | 同上；关联 `RSK-003` |
| P-08 | TOML 保留结构解析：选库或自实现 | 15 | 同上 |
| P-09 | JSON 保留结构解析：选库或自实现 | 15 | 需保留键顺序与数字字面量原文 |
| P-10 | Web 数据获取与缓存层 | 20 | 需支持游标分页、并发去重、按键失效 |
| P-11 | Web 图表库 | 20 | 单一库，不混用；需支持时间序列与主题 |
| P-12 | OpenAPI 到 TypeScript 的生成工具 | 20 | 产物需类型安全且可重复生成 |
| P-13 | 唯一来源计数的数据结构（精确集合或基数估计） | 17 | 不得存储完整 IP |
| P-14 | 容量测试的负载生成方式 | 18 | 需能同时驱动 8 节点 64 实例 |

上表本身不构成决策；实现阶段做出选择后必须在第 4 节登记完整条目。

## 4. 决策记录

## DL-001 使用 xUnit 与内置断言作为后端测试基础

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-08-03 |
| 阶段 | 10 |
| 决策 | 使用 xUnit 2.9.3、`Microsoft.NET.Test.Sdk` 与 xUnit 内置断言；按 Trait 分类，不在工程基础阶段引入替身库 |
| 背景 | 需要可由统一入口筛选单元、架构与集成测试，并控制并行行为 |
| 备选 | MSTest；NUnit；额外断言库与替身库 |
| 理由 | xUnit 的 Trait 与集合机制满足当前要求；内置断言足以覆盖阶段 10，避免没有实际用例的依赖 |
| 影响 | `Directory.Packages.props`、`Directory.Build.targets`、`tests/**` |
| 可逆性 | 中；更换框架会改写全部测试入口与属性 |
| 关联 | P-01 |

## DL-002 直接校验项目图与程序集约束

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-08-03 |
| 阶段 | 10 |
| 决策 | 架构测试直接解析项目 XML 并检查已加载程序集引用，不引入第三方架构测试库 |
| 背景 | 阶段 10 需要验证项目依赖方向、层边界和核心类型约束 |
| 备选 | NetArchTest；ArchUnitNET；自定义 Roslyn 分析器 |
| 理由 | 当前规则以项目引用图和少量 IL 可见约束为主，直接校验更小、更透明，失败信息可稳定对应 ARC 编号 |
| 影响 | `tests/Servitium.Domain.Tests/ArchitectureTests.cs` |
| 可逆性 | 高；后续约束复杂时可在保留测试编号的前提下替换实现 |
| 关联 | P-02、ARC-001 … ARC-006、ARC-010 |

## DL-003 使用 Microsoft 扩展日志与源生成消息

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-08-03 |
| 阶段 | 10 |
| 决策 | 使用 `Microsoft.Extensions.Logging` 的命名占位符和 `LoggerMessage` 源生成器；阶段 10 不增加额外日志供应程序 |
| 背景 | Server 与 Agent 需要结构化启动日志，且构建必须满足性能分析器和秘密不落日志的约束 |
| 备选 | Serilog；NLog；直接调用扩展方法生成消息 |
| 理由 | 平台内置抽象满足当前主机日志和源生成要求，后续可在组合根加入供应程序而不改变调用端 |
| 影响 | `src/Servitium.Server/HostLog.cs`、`src/Servitium.Agent/AgentLog.cs` |
| 可逆性 | 高；日志调用保留标准抽象，可替换输出供应程序 |
| 关联 | P-03 |

## DL-004 实体使用受控类且值对象使用 sealed record

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-08-03 |
| 阶段 | 11 |
| 决策 | 有身份和生命周期的聚合使用 get-only 标识与显式领域方法；纯值使用 sealed record；审计实体使用仅 getter 的 sealed class |
| 背景 | 需要同时表达基于 Guid 的实体身份、值相等性、状态转换和审计不可变性 |
| 备选 | 所有模型都使用可变 class；所有模型都使用位置 record |
| 理由 | 分离两种语义可让非法状态无法通过属性赋值产生，并避免 record 的公共 init setter 破坏审计约束 |
| 影响 | `src/Servitium.Domain/**`、UNT-100、UNT-110、UNT-170、ARC-021 |
| 可逆性 | 中；持久化映射需遵循 get-only 属性和构造函数 |
| 关联 | REQ-PLT-001、REQ-SEC-080 |

## DL-005 用例返回稳定错误码结果且基础设施能力全部经端口

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-08-03 |
| 阶段 | 11 |
| 决策 | 用例使用 `ApplicationResult<T>` 表达成功或稳定错误码；时间、标识、存储、Agent、日志、路径、出站请求和长操作均通过 Application 端口注入 |
| 背景 | Application 不得依赖基础设施，也不得产生已本地化用户文案 |
| 备选 | 以异常表示全部预期失败；在用例中直接调用框架 API |
| 理由 | 显式结果便于 REST、gRPC 与 WinUI 客户端一致映射，端口保持依赖方向并便于合成测试 |
| 影响 | `src/Servitium.Application/Common`、`Abstractions`、各核心流程用例 |
| 可逆性 | 中；后续可增加结果元数据但不改变稳定错误码语义 |
| 关联 | REQ-I18N-003、REQ-PLT-021 |

## DL-006 分页游标编码稳定复合键

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-08-03 |
| 阶段 | 11 |
| 决策 | 游标使用排序键与 Guid 的稳定 JSON 结构，经 UTF-8 Base64URL 编码；后续页按该复合键严格向后筛选 |
| 背景 | 所有列表接口禁止 offset 分页，且数据变化时不得重复或遗漏既有项目 |
| 备选 | offset；仅编码 Guid；数据库提供者专有游标 |
| 理由 | 复合键可处理相同排序值并保持协议不透明，算法不依赖持久化实现 |
| 影响 | `src/Servitium.Application/Common/CursorPagination.cs`、UNT-200 |
| 可逆性 | 中；已发出的游标在编码格式变化时需要版本兼容 |
| 关联 | REQ-PLT-022 |

## DL-007 使用 Konscious Argon2id 并独立实现 RFC 6238 TOTP

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-08-03 |
| 阶段 | 12 |
| 决策 | 密码哈希使用 `Konscious.Security.Cryptography.Argon2` 1.3.1 并把参数编码进存储串；TOTP 使用平台 HMAC-SHA1 独立实现 RFC 6238，秘密经 DPAPI 机器范围保护 |
| 背景 | 首次初始化、密码登录和可选 TOTP 需要可持久化、可验证且不依赖厂商在线服务的实现 |
| 备选 | 其他 Argon2 包；通用 OTP 包；把 TOTP 秘密明文写入 SQLite |
| 理由 | 选定包可明确设置 64 MiB、3 次迭代和并行度 2；TOTP 算法范围小且有 RFC 向量，平台 HMAC 与固定时间比较足够透明；DPAPI 避免数据库内明文秘密 |
| 影响 | `Infrastructure/Security/SecurityPrimitives.cs`、身份持久化与 INT-120、SEC-003、SEC-004 |
| 可逆性 | 中；更换密码格式需保留旧哈希验证和渐进升级 |
| 关联 | P-04、P-05、REQ-IDN-003、REQ-IDN-020 … REQ-IDN-022 |

## DL-008 隔离测试使用双回环监听分离 REST 与 Agent gRPC

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-08-03 |
| 阶段 | 12、13 |
| 决策 | 默认 Server 仍只监听一个回环 HTTP 地址；隔离开发测试可配置第二个仅回环 HTTP/2 明文端口承载 Agent gRPC，REST 保持 HTTP/1.1 回环端口 |
| 背景 | 本机测试既要运行 WinUI REST 客户端和真实 Agent 双向 gRPC，又不能信任开发证书、修改系统证书库或开放非回环地址 |
| 备选 | 安装并信任开发证书；让同一明文端口协商 HTTP/1.1 与 HTTP/2；仅使用进程内替身 |
| 理由 | 双回环端口无需管理员权限和系统修改，并能实际运行两个宿主进程；非回环 TLS/可信代理规则不受影响 |
| 影响 | `Server/Hosting/ServerHostOptions.cs`、`Server/Program.cs`、Server/Agent 进程级集成测试与用户测试环境 |
| 可逆性 | 高；正式部署可继续使用受信 TLS 或可信反向代理配置 |
| 关联 | REQ-SEC-020、REQ-NOD-021 |

## DL-009 HMCL 内容使用 SHA-1 CAS 与 SQLite 原子 Channel 指针

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-08-03 |
| 阶段 | 17 |
| 决策 | Release 文件按 SHA-1 内容寻址存放，元数据与引用计数进入 SQLite；发布前逐项重哈希，在单一写事务中切换 Channel 当前 Release |
| 背景 | HMCL manifest 的强 ETag 和文件哈希均以 SHA-1 为可观察契约，同时要求 Release 不可变和 Channel 原子切换 |
| 备选 | 每个 Release 复制完整目录；只把内容存为数据库 BLOB；覆盖可变发布目录 |
| 理由 | CAS 避免重复内容，流式物化控制内存，事务指针切换避免客户端观察到半发布状态；公开与私有 Channel 共用不可变 Release 和原子指针语义，私有分发另由令牌边界保护 |
| 影响 | `Infrastructure/Distribution`、`Infrastructure/Persistence/HmclRows.cs`、`Server/Hmcl`、INT-1300 … INT-1410 |
| 可逆性 | 低；内容布局与数据库迁移会成为持久化格式 |
| 关联 | REQ-HMC-010 … REQ-HMC-042 |

## DL-010 properties 使用保留结构的轻量解析文档

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-08-03 |
| 阶段 | 15 |
| 决策 | `properties` 使用仓库内的行级保留结构文档：未修改时原字节返回，简单键修改时保留 BOM、换行、注释、空行、顺序和分隔符；续行值暂不允许结构化修改 |
| 背景 | EULA 和 `server.properties` 编辑需要无损往返；后续 JSON、YAML、TOML 由独立的保留结构文档实现，避免把格式差异塞入同一解析器 |
| 备选 | `System.Collections.Specialized` 风格键值重写；引入通用 Java properties 包；提前实现所有格式 |
| 理由 | 小型实现已由字节级回归覆盖，能安全支撑 EULA；拒绝不确定的续行编辑比破坏原文件更符合无损要求 |
| 影响 | `Agent/Configurations/PreservingPropertiesDocument.cs`、`InstanceLifecycleService.cs`、INT-570、INT-900 |
| 可逆性 | 中；可在保持字节级测试的前提下替换解析器 |
| 关联 | P-06、REQ-CFG-020、REQ-INS-064 |

## DL-011 OpenAPI 由端点元数据生成并反向约束共享 Client

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-08-04 |
| 阶段 | 12、20 |
| 决策 | Minimal API 路由声明准确的请求、成功响应、文件流和 Problem Details 元数据；生成器据此产出 Client 操作契约，并同时检查 OpenAPI 中没有 Client 未覆盖的操作、Client 中没有 OpenAPI 不存在的操作 |
| 背景 | 仅检查路由名称会让 145 个路径看似存在，但所有操作仍可能退化为无模型 `200`，无法生成类型安全客户端 |
| 备选 | 手工维护第二份 OpenAPI；只验证路由允许列表；在生成产物中修补响应模型 |
| 理由 | 端点元数据是运行时语义的最近来源，双向集合校验能防止 Server 与 Client 任一侧静默漂移，生成产物不掩盖源契约缺失 |
| 影响 | `Server/OpenApi`、所有端点映射、`Client/Generated/GeneratedProtocolContract.g.cs`、`eng/Export-OpenApi.ps1`、`eng/Generate.ps1` |
| 可逆性 | 中；可替换生成实现，但必须保留同等的双向和响应语义校验 |
| 关联 | REQ-PLT-010、REQ-PLT-020、REQ-PLT-022、PB-022 |

## DL-012 Desktop 通知暂停显式区分持久化与进程内状态

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-08-04 |
| 阶段 | 22 |
| 决策 | 通知暂停建模为未暂停、定时截止和直到当前进程结束三种状态；只持久化未来的定时截止，直到重启状态永不跨 Desktop 启动恢复 |
| 背景 | 用可空截止时间同时表达无限暂停和未暂停会产生歧义，且设置重载会把“直到重启”错误延续到下一进程 |
| 备选 | 使用特殊日期哨兵；跨启动持久化所有暂停；关闭 Desktop 时主动清理一个模糊字段 |
| 理由 | 判别状态直接表达用户语义，能独立测试取消、到期和进程生命周期，并避免依赖正常退出清理 |
| 影响 | `Desktop/Services/NotificationPauseState.cs`、`DesktopNotificationService.cs`、`DesktopSettingsService.cs`、`MainWindow.xaml.cs` |
| 可逆性 | 高；仅影响 Desktop 本地设置表示 |
| 关联 | REQ-DSK-030、REQ-DSK-040 … REQ-DSK-043、PB-023 |

## DL-013 Desktop 以 UI 契约驱动页面并在本地执行最小权限预检查

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-08-04 |
| 阶段 | 20、22 |
| 决策 | 30 个页面由共享 `ui-contract.json` 描述字段、动作和状态，Desktop 使用统一页面模型与动作分派；页面进入前按角色和精确实例 grant 做最小预检查，Server 仍执行最终授权 |
| 背景 | 逐页手写壳层容易出现字段漂移和空占位；仅依赖 Server 拒绝会让普通用户先进入危险页面，降低可理解性但不能取代后端安全边界 |
| 备选 | 每页复制独立 XAML 与请求代码；Desktop 不做任何预检查；Desktop 直接引用 Infrastructure |
| 理由 | 共享契约和 Client 保持协议边界并让 30 页可系统审计；本地预检查改善信息架构，后端复核避免把客户端当作可信边界 |
| 影响 | `Contracts/Ui/ui-contract.json`、`Desktop/Views`、`Desktop/ViewModels`、`Desktop/Models/DesktopPageAuthorization.cs`、`Servitium.Client` |
| 可逆性 | 中；页面可逐步专用化，但字段、动作和权限必须继续由契约验证 |
| 关联 | REQ-DSK-001 … REQ-DSK-050、PB-025、PB-032 |

## DL-014 Agent 生命周期停止排空退出监视并抑制新重启

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-08-04 |
| 阶段 | 14 |
| 决策 | 为每个实例跟踪退出监视任务；宿主停止时先置停止标志、终止实例，再等待全部监视任务完成，且停止期间不再安排自动重启 |
| 背景 | 只等待进程退出会让后台状态持久化继续占用数据库，也可能在宿主停止窗口重新启动实例 |
| 备选 | 测试清理时重试删除数据库；忽略后台异常；用固定等待延迟宿主退出 |
| 理由 | 显式任务所有权给出确定的生命周期边界，不依赖时序或放宽清理断言，并保证服务停止不会生成新进程 |
| 影响 | `Agent/Lifecycle/InstanceLifecycleService.cs`、生命周期与进程级测试 |
| 可逆性 | 低；这是服务安全停止和资源所有权语义 |
| 关联 | REQ-INS-042、REQ-INS-066、PB-030、PB-031 |

## DL-015 未打包 Desktop 本地化使用 ResourceLoader 优先与 JSON 回退

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-08-04 |
| 阶段 | 22 |
| 决策 | Desktop 优先使用 Windows App SDK `ResourceLoader`；当未打包宿主没有默认资源上下文时，按当前语言读取 Contracts 输出的 `Localization/{language}.json`，未知键返回稳定键名 |
| 背景 | `WindowsPackageType=None` 的 WinUI 进程可能在资源加载器构造或读取时返回 `FileNotFoundException`/`NamedResource`，不能让托盘和主窗口启动失败 |
| 备选 | 在开发环境跳过所有本地化；复制第二套 Desktop 文案；把异常吞掉并显示空字符串 |
| 理由 | 保持正式 PRI 资源优先，同时复用已经由 `Generate.ps1` 校验的共享双语资源；稳定键回退便于诊断且不伪造文案 |
| 影响 | `Desktop/Services/LocalizationService.cs`、`App.xaml.cs`、`MainWindow.xaml.cs`、Desktop 本地化回归测试 |
| 可逆性 | 高；资源包可用时仍走原路径，回退仅覆盖未打包宿主 |
| 关联 | REQ-DSK-030、REQ-DSK-041、FA-N01 … FA-N05、PB-037、PB-038 |

## DL-016 Desktop 响应式布局使用隔离 UI Automation 启动检查

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-08-04 |
| 阶段 | 22 |
| 决策 | 在不读取用户数据的前提下，为 Desktop 增加仅隔离启动可用的 `--ui-layout-test`，由 `eng/Check-DesktopUi.ps1` 通过 UI Automation 和窗口截图检查 compact/normal/wide 的控件边界、可见性、重叠、焦点、按钮调用与本地化展示；测试数据根必须位于 `artifacts/test-runs`。 |
| 背景 | 仅测试窗口宽度到断点名称无法发现侧栏裁切、表单收缩、命令重叠和稳定值泄漏；用户真实启动截图证明必须观察实际 WinUI 控件。 |
| 备选 | 只保留 `WindowStateService.Breakpoint` 单元测试；在生产启动中读取用户数据；通过静态截图或构建成功替代 UI 运行。 |
| 理由 | UIA 在真实 WinUI 进程中检查可交互控件几何与键盘焦点，截图提供人工核对材料；隔离开关和根目录限制避免改变正式设置或数据。 |
| 影响 | `MainWindow.xaml`、`MainWindow.xaml.cs`、`App.xaml.cs`、`ContractPage.cs`、`eng/Check-DesktopUi.ps1`、动态验收记录；系统 DPI 切换仍需在具备相应环境时单独验证。 |
| 可逆性 | 高；该开关只在隔离参数同时存在时接受，不改变普通启动默认最小窗口或数据根。 |
| 关联 | PB-039、REQ-DSK-001 … REQ-DSK-050、FA-J01 … FA-J14 |

## 5. 更新记录

| 日期 | 更新内容 | 阶段 |
| --- | --- | --- |
| 2026-08-02 | 建立文件并登记待决策清单 | 00 |
| 2026-08-03 | 登记阶段 10 的测试、架构校验与日志实现决策 | 10 |
| 2026-08-03 | 登记阶段 11 的领域对象、用例结果与分页游标决策 | 11 |
| 2026-08-03 | 登记身份密码/TOTP、双回环测试宿主、HMCL 内容库与 properties 保留结构实现决策 | 12、13、15、17 |
| 2026-08-04 | 登记 OpenAPI 双向契约、通知暂停状态、UI 契约与权限预检查、Agent 退出监视所有权、未打包 Desktop 本地化回退决策 | 12、14、20、22 |
