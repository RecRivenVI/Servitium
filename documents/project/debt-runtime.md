# 运行与协议技术债

按可见影响排序；本轮不改变已有产品行为或修复整理前的失败。

| 规则/位置 | 影响 | 建议 |
| --- | --- | --- |
| T-04、C-09；Client 的 SignalR 传递依赖及相关锁文件 | NU1903 阻断还原/构建，公告 GHSA-334f-vvp4-7g6p；Client/Desktop 测试无法比较 | 单独安排依赖安全修复，不禁用审计或降低警告 |
| X-04/X-05、T-03；WindowsPathPolicy、LocalAuthenticationPipeService、进程宿主 | 服务/ACL/第二平台和真实故障恢复没有当前完整证据；管道“天然不跨网络”的旧说明错误 | 保留本机 PID/SID/路径检查，并专门验证远程拒绝/ACL；按真实场景定义执行 |
| G-03/F-01；RequestSecurity、EventsConnection 与 protocol-control_plane | CSRF 实际 X-CSRF-Token/400，旧文字为另一头名/403；双方实现一致但契约漂移 | 裁定后一次同步，不本轮改接口行为 |
| G-03、T-03；agent-control.proto / AgentCommandDispatcher | protobuf 信封为 bytes JSON 载荷，旧文字称按类型 protobuf；兼容性检查不覆盖 DTO 变化 | 明确协议真实边界后补兼容回归 |
| C-03/C-07、DN-07；InstanceLifecycleService、InstanceFileService、HmclDistributionStore、SystemManagementEndpoints | 大服务多职责；已有竞态/失败历史，当前少量回归不能覆盖长期环境 | 在实际场景下按行为分拆，不加新兼容层 |
| T-02/T-03；保留结构配置与模组解析测试 | 合成样本不能证明所有真实格式无损往返 | 每格式真实样本矩阵、真实 JAR 兼容与失败原文件保持 |
| DN-07；AgentCommandDispatcher、RunCredentialStore、DatabaseMigrationHostedService | 直接 UtcNow 与统一时间端口不一致 | 后续按真实时序测试收敛，不新增无用接口 |

既有 PB-001～038/PB-040 修复结论保留在外部原记录。本轮没有重新宣称关闭，也未把它们一概标为回归失败。已退役的覆盖率/Profile 缺口不再作为待实现门禁。
