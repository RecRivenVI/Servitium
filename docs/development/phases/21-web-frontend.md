# 阶段 21：Web 前端

## 1. 阶段目的

实现 `Servitium.Web` 的全部页面，达到 `docs/design/information-architecture.md` 与 `web-specification.md` 定义的规格。

## 2. 需求编号

全部界面相关需求的 Web 侧实现：`REQ-IDN-*`（界面）、`REQ-NOD-*`、`REQ-JRE-*`、`REQ-INS-*`、`REQ-TRM-*`、`REQ-FIL-*`、`REQ-CFG-*`、`REQ-MOD-*`、`REQ-SCH-*`、`REQ-USR-*`、`REQ-HMC-*`（界面）、`REQ-I18N-*`。

## 3. 前置条件

* 阶段 20 完成条件全部满足。

## 4. 范围

`information-architecture.md` 第 1 节列出的全部页面（PG-001 … PG-063）的 Web 实现，含：

* 页面结构、区块顺序、字段、动作、状态、错误、空态；
* 终端（xterm.js）与代码编辑器（Monaco）集成；
* 分片上传与断点续传；
* 长操作进度与取消；
* 实时事件驱动的界面更新；
* 仪表盘编辑模式；
* 响应式与无障碍。

## 5. 非目标

* 不实现 Desktop（阶段 22）；
* 不做双端一致性的最终比对（阶段 23）；
* 不做用户验收（阶段 24）。

## 6. 需要创建的项目或目录

在 `src/Servitium.Web/src/features/` 下补齐各模块的 `components/`、`hooks/`、`model/`、`routes/`。

模块清单：`auth`、`dashboard`、`nodes`、`instances`、`terminal`、`files`、`configurations`、`mods`、`schedules`、`users`、`settings`、`audit`、`operations`、`hmcl`。

## 7. 需要实现的类型或模块

按页面编号分组，逐页实现。每页必须：

* 结构与 `information-architecture.md` 中该页的规格一致；
* 使用 `shared-ui-specification.md` 的组件语义与交互模式；
* 为契约中登记的每个区块、字段、动作添加 `data-servitium-*` 标记（供一致性测试采集）；
* 全部文案经 i18n；
* 全部 API 调用经生成客户端；
* 错误按 `code` 呈现。

## 8. 数据模型

无服务端变更。

## 9. API 或事件

消费既有接口。事件订阅按页面作用域建立与释放。

## 10. 实施顺序

按依赖与价值排序，逐组完成并在每组结束时运行端到端测试：

1. **认证组**：PG-001 初始化、PG-002 登录、PG-003 两步验证、PG-032 个人设置；
2. **节点组**：PG-020 节点列表、PG-021 节点详情、PG-022 Java 运行时；
3. **实例列表组**：PG-010 实例列表、PG-011 我的实例、PG-019 新建实例向导；
4. **控制台组**：PG-012 实例控制台（含终端集成）、PG-018 实例设置；
5. **文件组**：PG-013 文件管理（含分片上传、下载、归档、编辑器集成）；
6. **配置组**：PG-014 配置总览、PG-015 配置编辑（含结构化表单与原始文本切换）；
7. **模组组**：PG-016 模组与插件；
8. **计划任务组**：PG-017 计划任务（含新建/编辑对话框与历史抽屉）；
9. **治理组**：PG-030 用户管理、PG-031 用户授权、PG-042 操作日志、PG-043 安全审计、PG-044 长操作；
10. **仪表盘与设置组**：PG-040 仪表盘（含编辑模式与全部卡片）、PG-041 系统设置（全分组）；
11. **分发组**：PG-060 分发总览、PG-061 Channel 详情、PG-062 新建发布、PG-063 Release 详情；
12. **其他**：PG-050 公开信息页、PG-051 未找到；
13. 响应式适配全量走查；
14. 无障碍走查；
15. 性能优化（代码分割、虚拟滚动、包体积）；
16. 一致性测试纳入全部页面；
17. 运行 `eng/Validate.ps1`。

每组完成后即把该组页面纳入一致性测试与端到端测试，不留到最后统一补。

## 11. 关键算法

| 算法 | 说明 |
| --- | --- |
| 分片上传（前端） | 切片 → 并发 3 片 → 每片计算校验和 → 失败重试 → 会话标识存 `sessionStorage` 以便刷新后续传 |
| 终端渲染 | xterm.js + WebGL；缓冲 5000 行；自动滚动与暂停；搜索；尺寸变化防抖 200 ms |
| 长操作跟踪 | 发起后订阅 `operation:{id}` 作用域；进度、阶段、结果；完成后失效相关查询 |
| 仪表盘布局编辑 | 拖拽排序 + 栅格宽度调整 + 高度档位；保存前校验总宽度不超过 12 每行 |
| 差异预览渲染 | 三组清单分段展示；每组可折叠；「将删除」以危险样式；超过 200 条时虚拟滚动 |
| 结构化配置表单 | 从 `GET /configurations/schema/{key}` 取字段定义 → 按类型渲染控件 → 未在定义中的键以通用文本控件展示并保留位置 |

## 12. Windows 平台事项

无（Web 与平台无关）。浏览器支持见 `web-specification.md` 第 12 节。

## 13. 安全要求

| 项 | 说明 |
| --- | --- |
| CSP | 全部页面在 CSP 下无错误；Monaco worker 通过 `worker-src` 放行 |
| XSS | 禁止 `dangerouslySetInnerHTML`；用户可控内容渲染前消毒 |
| CSRF | 所有非幂等请求携带令牌 |
| 内嵌卡片 | 仅白名单来源；不传递凭据 |
| 权限 | 无权限时展示说明页而非 404；界面不渲染无权限的动作 |
| 秘密 | 密码、令牌、Key 输入框默认隐藏；创建后的明文仅展示一次并提示复制 |

## 14. 自动化测试

| 层 | 内容 |
| --- | --- |
| 单元 | 格式化、映射、查询键、分片切分、差异计算的前端呈现逻辑 |
| 组件 | 表格、上传任务行、终端容器、确认对话框（含键名确认）、结构化表单控件 |

## 15. 集成测试

Playwright 端到端（分类 `EndToEnd`），跑在真实 Server + 真实 Agent + `JavaServerFixture` 上。

必须实现 `web-specification.md` 第 16 节的 `WEB-001` … `WEB-020` 全部场景，另加：

| 编号 | 内容 |
| --- | --- |
| `WEB-021` | 每个页面的区块顺序与契约一致（一致性测试） |
| `WEB-022` | 每个页面的空态、加载态、错误态、权限不足态可触发并正确呈现 |
| `WEB-023` | 每个破坏性操作的确认对话框正确（含键名确认） |
| `WEB-024` | 长操作的进度、取消、失败呈现 |
| `WEB-025` | 终端：输出、输入、快捷命令、搜索、导出、独立视图 |
| `WEB-026` | 文件：上传中断续传后哈希一致 |
| `WEB-027` | 配置：结构化编辑保存后原文件注释与顺序保留（通过 API 复核字节） |
| `WEB-028` | 分发：发布预览的三组清单与后端一致；键名确认生效 |
| `WEB-029` | 仪表盘编辑：拖拽、调整、隐藏、保存、重置 |
| `WEB-030` | 语言切换后全部可见文案变化且无未翻译键 |

## 16. 人工测试

无。用户验收在阶段 24。

## 17. 调试启动方式

```powershell
dotnet run --project src/Servitium.Server -- --environment Development
dotnet run --project src/Servitium.Agent -- --environment Development
npm run dev --prefix src/Servitium.Web
# 浏览器访问 http://127.0.0.1:5173
```

端到端测试：

```powershell
./eng/Test.ps1 -Category EndToEnd
```

## 18. 验收责任方

编码 Agent 自主验收。

## 19. 完成条件

1. `information-architecture.md` 第 1 节的全部页面已实现；
2. 每页的区块顺序、字段、动作、状态与契约一致（一致性测试通过）；
3. `WEB-001` … `WEB-030` 全部通过；
4. ESLint（`--max-warnings=0`）、Prettier、`tsc --noEmit` 通过；
5. 包体积在预算内；
6. 首屏可交互 < 2 秒（本地网络）；
7. 无障碍走查通过：键盘可达、对比度、可访问名称、实时区域；
8. 语言切换无未翻译键；
9. CSP 下无控制台错误；
10. `eng/Validate.ps1` 全量通过。

## 20. 禁止提前进入下一阶段的条件

* 存在未实现的页面；
* 一致性测试存在差异；
* 存在硬编码的用户可见字符串；
* 存在手写的 API 调用；
* 无障碍走查未通过；
* 端到端测试存在随机失败。

## 21. 需要更新的动态记录

* `docs/records/current-status.md`
* `docs/records/development-log.md`
* `docs/records/decision-log.md`
* `docs/records/problem-log.md`
* `docs/records/validation-log.md`
* `docs/testing/requirements-traceability.md`
* `docs/testing/acceptance-matrix.md`
