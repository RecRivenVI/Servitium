# Web 界面规格

`Servitium.Web`：React + TypeScript + Fluent UI React v9 + Vite，构建产物由 `Servitium.Server` 托管。

页面结构见 `../design/architecture-information.md`；共享设计体系见 `../design/principle-user_interface.md`。本文件只定义 Web 特有的部分。

## 1. 技术约束

| 项 | 规范 |
| --- | --- |
| 框架 | React（函数组件 + Hooks），不使用类组件 |
| 语言 | TypeScript，`strict` 全开，禁止 `any`；边界数据使用 `unknown` 并显式收窄 |
| 组件库 | Fluent UI React v9；**不引入其他 UI 组件库** |
| 构建 | Vite |
| 路由 | 客户端路由，路径与 `../design/architecture-information.md` 一致 |
| 数据获取 | 统一的查询层（缓存、失效、重试、并发去重） |
| API 客户端 | 由 OpenAPI 生成到 `src/api`，**不手工编辑，不手写 fetch** |
| 实时 | SignalR 客户端，集中管理连接与订阅 |
| 状态 | 服务端状态由查询层持有；界面局部状态用组件内 state；跨页面的少量全局状态用轻量 store |
| 样式 | Fluent UI 的样式方案（`makeStyles`）；不引入 CSS 框架 |
| 图标 | Fluent 图标包 |
| 终端 | xterm.js |
| 编辑器 | Monaco Editor |
| 图表 | 单一图表库（在阶段 20 选定并登记到 `../design/adr-2-xunit.md`），不混用 |

## 2. 目录结构

```text
web/
├── package.json
├── vite.config.ts
├── tsconfig.json
├── index.html
├── public/
└── src/
    ├── app/            应用外壳、路由表、主题、错误边界、全局提示
    ├── features/       按领域分的功能模块
    ├── shared/         跨领域组件、hooks、格式化、常量
    ├── api/            生成的客户端（只读）
    ├── realtime/       SignalR 连接与订阅
    └── i18n/           生成的文案资源
```

`features/` 下的模块：`auth`、`dashboard`、`nodes`、`instances`、`terminal`、`files`、`configurations`、`mods`、`schedules`、`users`、`settings`、`audit`、`operations`、`hmcl`。

每个模块内部：

```text
features/<模块>/
├── components/   仅本模块使用的组件
├── hooks/        数据获取与业务 hooks
├── model/        类型别名、映射、常量
└── routes/       页面组件
```

模块之间**不直接互相引用**；共享内容上移到 `shared/`。该约束由 lint 规则守护。

## 3. 托管与路由

| 项 | 规范 |
| --- | --- |
| 静态资源 | `Servitium.Server` 从 `wwwroot/` 提供 |
| SPA 回退 | 非 `/api`、`/hubs`、`/hmcl`、`/health`、`/swagger` 前缀的 GET 请求回退到 `index.html` |
| 路径前缀 | 支持部署在子路径下（读取服务端注入的基路径） |
| 缓存 | 带内容哈希的资源 `immutable` 长缓存；`index.html` 为 `no-cache` |
| 压缩 | 由服务端统一处理（HMCL 端点除外） |

## 4. 认证与会话

| 项 | 规范 |
| --- | --- |
| 凭据 | 会话 Cookie（`Secure`、`HttpOnly`、`SameSite=Strict`）；前端不接触令牌 |
| CSRF | 启动时获取 CSRF 令牌，所有非幂等请求携带 `X-Servitium-Csrf` |
| 未认证 | 收到 `401` 时清理本地缓存并跳转登录页，保留原目标以便登录后返回 |
| 权限变更 | 收到 `authorization.changed` 事件时重新拉取权限并刷新受影响页面；若当前页面不再可访问则跳转到可访问页面并提示 |
| 会话过期 | 服务端返回 `401` 时统一处理，不做静默重试 |

## 5. 数据获取约定

| 约定 | 说明 |
| --- | --- |
| 查询键 | `[资源, 参数对象]`，参数对象稳定序列化 |
| 缓存时间 | 列表 30 秒、详情 15 秒、设置 5 分钟；实时事件到达时主动失效 |
| 失效 | 变更操作成功后失效相关查询键；实时事件按作用域失效 |
| 重试 | 仅对网络错误与 5xx 重试（最多 2 次，指数退避）；4xx 不重试 |
| 并发去重 | 相同查询键的并发请求合并 |
| 分页 | 游标分页；列表使用「加载更多」或无限滚动，不使用页码跳转（后端为游标分页） |
| 乐观更新 | 仅用于启用/停用等可逆的轻量切换；失败时回滚并提示 |

## 6. 实时连接

| 项 | 规范 |
| --- | --- |
| 连接 | 应用启动后建立 `/hubs/v1/events`；进入实例控制台时额外建立 `/hubs/v1/terminal` |
| 订阅 | 按当前页面订阅所需作用域；离开页面时取消订阅 |
| 重连 | 自动重连（指数退避，上限 30 秒）；重连后用 `lastEventId` 补发 |
| 断线呈现 | 页头持续提示条，说明「实时更新已暂停」并显示重连倒计时 |
| 背压 | 收到「部分输出被丢弃」标记时在终端插入可见标记行 |

## 7. 终端

| 项 | 规范 |
| --- | --- |
| 渲染 | xterm.js + WebGL 渲染器（不可用时回退 Canvas） |
| 缓冲 | 前端保留最近 5000 行；超出时丢弃最旧 |
| 输入 | 独立输入框（不直接在终端内输入），支持历史与快捷命令按钮 |
| 自动滚动 | 默认开启；向上滚动时暂停并显示「回到底部」 |
| 搜索 | `Ctrl+F` 打开搜索条，支持上一个/下一个与高亮 |
| 尺寸 | 容器尺寸变化时防抖 200 ms 后发送 `Resize` |
| 导出 | 生成 UTF-8 文本文件并触发下载，可选是否保留 ANSI 序列 |
| 独立视图 | 新标签页打开 `/instances/{id}/terminal`，无侧栏，仅终端 |

## 8. 编辑器

| 项 | 规范 |
| --- | --- |
| 加载 | Monaco 按需加载，worker 从同源加载（配合 CSP 的 `worker-src 'self' blob:`） |
| 语言 | properties、json、yaml、toml、log、plaintext；按扩展名映射 |
| 主题 | 跟随应用主题 |
| 只读 | manifest 预览使用只读模式 |
| 未保存保护 | 路由离开与页面卸载时确认 |

## 9. 文件上传

| 项 | 规范 |
| --- | --- |
| 选择 | `<input type="file" multiple>` 与 `webkitdirectory`（目录上传） |
| 拖放 | 拖放到文件列表区；进入时高亮并显示目标目录 |
| 分片 | 默认 8 MiB；并发 3 片；每片计算校验和 |
| 断点续传 | 页面刷新后可从会话状态恢复（会话标识保存在 `sessionStorage`） |
| 进度 | 每个任务一行：文件名、进度、速率、剩余时间、暂停/取消 |
| 冲突 | 上传前检测同名文件并弹出「覆盖 / 保留两者 / 跳过」选择 |

## 10. 下载

| 项 | 规范 |
| --- | --- |
| 单文件 | 直接导航到下载端点（携带 Cookie），由浏览器处理 |
| 多文件与目录 | 触发打包长操作，完成后提供下载链接 |
| 大文件 | 依赖服务端 `Range` 支持，由浏览器自行续传 |

## 11. 响应式

断点与行为见 `../design/principle-user_interface.md` 第 2 节。Web 特有约定：

| 断点 | 导航 | 表格 | 终端 |
| --- | --- | --- | --- |
| `xs` | 抽屉式，汉堡按钮 | 转为卡片列表 | 全宽，输入框固定底部 |
| `sm` | 折叠为图标栏 | 保留关键列 + 横向滚动 | 全宽 |
| `md` 及以上 | 展开 | 完整列 | 按栅格 |

页面主体在 `xl` 断点下限制最大宽度（1680 px）并居中。

## 12. 浏览器支持

最近两个稳定版本的 Edge、Chrome、Firefox。不支持 IE 与 Safari（Safari 非目标平台，但不主动破坏）。

要求的浏览器能力：`fetch`、`WebSocket`、`ResizeObserver`、`IntersectionObserver`、`structuredClone`、CSS `:focus-visible`、CSS 逻辑属性。

## 13. 性能

| 指标 | 目标 |
| --- | --- |
| 首屏可交互 | 本地网络 < 2 秒 |
| 初始 JS 传输量（gzip 后） | < 400 KiB |
| 路由级代码分割 | 每个功能模块独立 chunk |
| Monaco 与 xterm | 按需加载，不进入初始 chunk |
| 列表渲染（50 条） | < 300 毫秒 |
| 长列表 | 超过 200 行使用虚拟滚动 |

构建时输出包体积报告；超过预算时构建告警，超过硬上限时失败。

## 14. 无障碍

除 `../design/principle-user_interface.md` 第 6 节的通用要求外：

| 项 | 规范 |
| --- | --- |
| 跳转链接 | 提供「跳到主内容」 |
| 路由变化 | 播报新页面标题并把焦点移到主内容 |
| 对话框 | 焦点陷阱、`Esc` 关闭、关闭后焦点回到触发元素 |
| 表格 | 正确的 `role`、列头关联、排序状态播报 |
| 实时区域 | 操作结果与连接状态用 `aria-live="polite"`；错误用 `assertive` |
| 终端 | 提供「以纯文本查看输出」的无障碍视图 |

## 15. 代码质量

| 项 | 规范 |
| --- | --- |
| Lint | ESLint（TypeScript、React、Hooks、无障碍规则）；`--max-warnings=0` |
| 格式 | Prettier；CI 校验 |
| 类型检查 | `tsc --noEmit` |
| 禁止 | `dangerouslySetInnerHTML`、手写 `fetch`/`axios`、跨 feature 直接导入、硬编码用户可见字符串、`console.log`（Release） |
| 强制 | 所有 API 调用经生成客户端；所有文案经 i18n；所有颜色经令牌 |

上述禁止项由 lint 规则实现并在 `dotnet test` 中执行。

## 16. 测试

| 层 | 工具与范围 |
| --- | --- |
| 单元 | 纯函数、格式化、映射、hooks 逻辑 |
| 组件 | 关键交互组件（表格、上传、终端容器、确认对话框） |
| 端到端 | Playwright，跑在真实 Server + 真实 Agent 上 |

关键端到端场景（`WEB-###`，详见 `../../validations/ui-clients/validation.md`）：

| 编号 | 场景 |
| --- | --- |
| `WEB-001` | 首次初始化 → 登录 → 仪表盘 |
| `WEB-002` | 生成注册令牌 → Agent 接入 → 节点在线 |
| `WEB-003` | 新建实例（三种方式各一） |
| `WEB-004` | 启动 → 终端输出 → 发送命令 → 停止 |
| `WEB-005` | 强制终止并验证进程树消失 |
| `WEB-006` | 文件上传（含中断续传）、下载、重命名、压缩、解压、删除 |
| `WEB-007` | 配置结构化编辑与无损保存、备份恢复 |
| `WEB-008` | 模组上传、停用、删除；运行中的文件锁提示 |
| `WEB-009` | 计划任务创建、启停、立即执行、历史 |
| `WEB-010` | 用户创建、授权、撤销后终端被关闭 |
| `WEB-011` | 仪表盘编辑、保存、重置 |
| `WEB-012` | 系统设置各分组保存与生效标注 |
| `WEB-013` | HMCL Channel 创建、发布、差异预览、回滚 |
| `WEB-014` | 长操作进度、取消、失败呈现 |
| `WEB-015` | 断线重连与事件补发 |
| `WEB-016` | 权限不足页面而非 404 |
| `WEB-017` | 窄屏布局与动作折叠 |
| `WEB-018` | 键盘可达性遍历 |
| `WEB-019` | 语言切换后全部界面文案变化 |
| `WEB-020` | 深色主题下的对比度检查 |

## 17. 与 Desktop 的差异

见 `../design/mechanism-desktop.md` 第 15 节。除该清单外，两端必须一致。
