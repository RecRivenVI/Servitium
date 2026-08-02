# 阶段 15：文件、配置与模组

## 1. 阶段目的

实现实例工作目录内的全部内容管理能力：文件浏览与增删改、分片上传与断点续传、流式下载与 `Range`、URL 下载与 SSRF 防护、压缩与解压、文本编辑与原子保存、配置文件的无损往返与备份、模组与插件的扫描与元数据解析。

## 2. 需求编号

`REQ-FIL-001` … `REQ-FIL-072`、`REQ-CFG-001` … `REQ-CFG-023`、`REQ-MOD-001` … `REQ-MOD-020`、`REQ-SEC-030`、`REQ-SEC-050`、`THR-030` … `THR-036`、`THR-040`。

## 3. 前置条件

* 阶段 14 完成条件全部满足。

## 4. 范围

* 文件浏览、新建、重命名、复制、移动、删除、批量操作；
* 分片上传会话与断点续传；
* 流式下载与 `Range`；
* URL 下载与取消，含 SSRF 防护；
* ZIP 压缩与解压，含编码选择与安全限制；
* 文本读取与原子保存，编码协商；
* 配置文件识别、结构化读写、无损往返、语法校验、写入前备份；
* `server.properties` 字段定义；
* 模组与插件扫描、元数据解析、启停、删除、上传、关联配置。

## 5. 非目标

* 不实现计划任务（阶段 16）；
* 不实现 HMCL（阶段 17）；
* 不实现前端界面。

## 6. 需要创建的项目或目录

```text
src/Servitium.Agent/
├── Files/            FileBrowser、FileOperations、TransferManager、UrlDownloader、ArchiveService
├── Configurations/   ConfigurationLocator、格式解析器、BackupStore、AtomicWriter
├── Mods/             ModScanner、MetadataReaders（各格式）
└── Quotas/           并发与速率限制

src/Servitium.Infrastructure/
└── Http/             扩展阶段 12 的受限出站 HTTP 客户端：流式下载、进度、大小上限

src/Servitium.Server/
├── Endpoints/Files
├── Endpoints/Configurations
├── Endpoints/Mods
└── Streaming/        上传分片与下载流的中转
```

## 7. 需要实现的类型或模块

### 7.1 文件操作

全部路径经阶段 13 的路径校验链。实现：列表（分页、搜索、排序）、卷状态、新建文件与目录、重命名、复制、移动、删除、批量。

目录级操作（复制、移动、删除）为长操作，逐条报告进度。

### 7.2 分片上传

* `POST .../uploads` 创建会话：返回 `UploadId`、分片大小、已收位图；
* `PUT .../uploads/{id}/parts/{index}`：校验分片校验和，落盘到暂存；
* `POST .../uploads/{id}/complete`：按序合并到临时文件 → 校验总大小 → 原子替换目标；
* `DELETE .../uploads/{id}`：取消并清理；
* 会话 24 小时过期，后台清理；
* 上传前磁盘空间预检。

控制平面负责会话记录与分片中转；Agent 负责落盘与合并。

### 7.3 下载

* 流式，支持 `Range` 与 `206`；
* 控制平面向 Agent 打开下载子流并把范围转发；
* 目录下载触发打包长操作，完成后提供下载。

### 7.4 URL 下载

* 长操作，进度上报，可取消；
* 每节点并发上限；
* 完整 SSRF 防护（见第 11 节）；
* 下载到临时文件后原子落位。

### 7.5 归档

* 压缩：选择条目、归档名、条目名编码；
* 解压：目标为当前目录或新目录、条目名编码、自动检测 UTF-8 标志位；
* 全部解包应用 `REQ-SEC-050` 的安全限制；
* 每节点并发上限与单归档解压总大小上限。

### 7.6 文本编辑

* 读取：按实例编码或临时指定编码解码；返回内容、编码、行尾模式、大小；
* 保存：编码回写 → 临时文件 → 刷盘 → 原子替换；
* 大小上限（默认 8 MiB）。

### 7.7 配置管理

#### 识别

按 `REQ-CFG-001` 的清单扫描；同一逻辑配置多个候选时标记冲突。

#### 格式解析器

| 格式 | 要求 |
| --- | --- |
| `properties` | 保留注释行、空行、键顺序、`\` 续行、原有分隔符（`=`/`:`）与转义形式 |
| JSON | 保留键顺序、缩进风格、数字字面量原始文本、尾随换行 |
| YAML | 保留注释、缩进风格、引号风格、锚点与别名 |
| TOML | 保留注释、表与键顺序、内联表与多行字符串形式 |
| 纯文本 | 字节级保留 |

每个解析器提供：`Parse(bytes, encoding) -> Document`、`GetValues(Document) -> KeyValues`、`SetValue(Document, key, value)`、`Write(Document) -> bytes`。

**核心不变量**：`Write(Parse(x)) == x`（字节级）。

#### 写入流程

```text
读取原字节 → 解析 → 应用修改 → 序列化 → 语法校验（重新解析一次）
→ 备份原文件 → 写临时文件 → 刷盘 → 原子替换
```

#### 备份

存放在 Agent 数据目录的 `backups/<实例>/<相对路径转义>/<时间戳>.bak`，每文件保留 10 份。

#### `server.properties` 字段定义

作为数据文件维护，含键、类型、默认值、取值范围或枚举、单位、说明、分组。随 `Servitium.Contracts` 分发给前端渲染表单。

### 7.8 模组与插件

扫描 `mods/` 与 `plugins/`；按 `REQ-MOD-003` 的优先级读取 JAR 内元数据；输出统一模型；解析失败降级为仅文件名并标注。

启停通过 `.disabled` 后缀；运行中禁用删除并对停用给出警告；文件被占用时返回 `file-locked`。

关联配置：按模组标识在 `config/` 下同名或前缀匹配。

## 8. 数据模型

控制平面新增 `UploadSession`。

Agent 本地新增 `LocalFileTask`、`LocalUploadPart`、`LocalConfigBackup`。

## 9. API 或事件

REST：`protocols-and-contracts.md` 3.6、3.7、3.8 的全部端点。

事件：`file.task-progress`、`operation.*`。

gRPC：文件、配置、模组组的全部指令；`file-upload` 与 `file-download` 子流。

## 10. 实施顺序

1. 文件浏览与卷状态；
2. 新建、重命名、删除（单项）；
3. 复制、移动（含目录，长操作）；
4. 批量操作；
5. 下载子流与 `Range`；
6. 分片上传会话、分片落盘、合并、断点续传、过期清理；
7. 磁盘空间预检；
8. 并发与速率限制；
9. 归档服务：压缩、解压、编码、安全限制；
10. 扩展阶段 12 的受限出站 HTTP 客户端，加入流式下载、进度与下载大小上限；
11. URL 下载与取消；
12. 文本读取与原子保存；
13. `properties` 保留结构解析器 + 往返测试；
14. JSON 保留结构解析器 + 往返测试；
15. YAML 保留结构解析器 + 往返测试；
16. TOML 保留结构解析器 + 往返测试；
17. 配置识别与冲突判定；
18. `server.properties` 字段定义文件；
19. 结构化读写 + 语法校验 + 备份 + 原子保存；
20. 把阶段 14 的 EULA 写入切换到 `properties` 解析器；
21. 模组扫描与各格式元数据读取；
22. 模组启停、删除、上传、关联配置；
23. 端点与 gRPC 指令；
24. 测试；
25. 运行 `eng/Validate.ps1`。

## 11. 关键算法

### 11.1 SSRF 防护

```text
1. 解析 URL；拒绝非 http/https
2. 规范化主机名（拒绝十进制、八进制、0x 形式的 IP 伪装）
3. DNS 解析得到全部候选 IP（A + AAAA）
4. 对每个 IP 判定：回环 / 链路本地 / 私有 / 保留 / 文档 / 组播 / 节点自身地址
   → 命中且主机名不在私有源白名单中 → 拒绝（url-not-allowed）
5. 选定一个通过校验的 IP，建立连接时直接绑定该 IP（Host 头保留原主机名）
6. 若响应为 3xx：解析 Location，跳数 +1（上限 5），回到步骤 1
7. 连接与读取超时固定；下载大小超出上限时中止
```

### 11.2 分片合并

```text
1. 校验全部分片已收且校验和匹配
2. 按序把分片写入临时文件
3. 校验临时文件总大小与声明一致
4. 原子替换目标（目标已存在时按覆盖策略处理）
5. 清理暂存
```

任一步失败即清理临时文件并保留会话供重试。

### 11.3 归档安全检查

见阶段 13 第 11 节，本阶段复用同一实现。

### 11.4 `properties` 保留结构解析

```text
Document = 有序的行列表，每行为：注释 / 空行 / 键值行（含原始分隔符、原始转义、原始续行结构）
SetValue：定位键值行 → 仅替换值部分 → 保留分隔符、行内空白与续行结构
新增键：追加到文档末尾（保留末尾换行风格）
删除键：移除该键值行及其续行
Write：逐行原样输出
```

同理适用于其他格式：修改只作用于目标节点，其余字节原样输出。

### 11.5 模组元数据解析

```text
按 7 种来源的优先级依次尝试：
  fabric.mod.json → quilt.mod.json → META-INF/neoforge.mods.toml
  → META-INF/mods.toml → mcmod.info → paper-plugin.yml → plugin.yml
首个成功解析的结果即为输出；全部失败时输出 { 显示名 = 文件名, 元数据不可读 = true }
```

## 12. Windows 平台事项

* 原子替换：`ReplaceFile` 或 `MoveFileEx` 带 `MOVEFILE_REPLACE_EXISTING`；跨卷时降级为复制 + 删除；
* 文件占用：捕获 `ERROR_SHARING_VIOLATION`（32）与 `ERROR_LOCK_VIOLATION`（33），映射为 `file-locked`；
* 卷剩余空间：`GetDiskFreeSpaceEx`；
* 长路径：内部使用 `\\?\`；
* 归档条目名编码：ZIP 的 UTF-8 标志位优先，否则按用户选择的代码页；
* 大小写：Windows 文件系统不区分大小写，重命名仅改大小写时需特殊处理（两步重命名）。

## 13. 安全要求

必须实现并测试：`THR-030`（路径穿越，复用阶段 13 实现并在本阶段的每个入口验证）、`THR-031`（文件名）、`THR-032` … `THR-034`（归档）、`THR-035`（磁盘耗尽）、`THR-036`（临时文件）、`THR-040`（复用阶段 12 的 SSRF 防护并覆盖 URL 下载入口）。

配置写入必须先备份后写入；备份失败即拒绝写入。

## 14. 自动化测试

单元测试：

| 编号族 | 内容 |
| --- | --- |
| `UNT-300` | SSRF 判定：各类地址、伪装形式、白名单 |
| `UNT-310` | 归档限制计算 |
| `UNT-320` | 分片位图与合并顺序 |
| `UNT-330` | 模组元数据解析（每种格式的真实样本） |
| `UNT-340` | `server.properties` 字段定义可解析且每字段有类型与说明 |

## 15. 集成测试

| 编号族 | 内容 |
| --- | --- |
| `INT-800` | 文件 CRUD 全流程；批量操作 |
| `INT-810` | 复制与移动（含跨卷降级） |
| `INT-820` | 分片上传：正常、乱序、重复分片、中断续传、会话过期清理 |
| `INT-830` | 上传后文件哈希与源一致 |
| `INT-840` | 下载 `Range`：单区间、越界返回 416、完整下载 |
| `INT-850` | URL 下载：成功、取消、并发上限、大小上限 |
| `INT-860` | 压缩与解压：UTF-8/GBK/Big5 条目名往返 |
| `INT-870` | 恶意归档：ZIP Slip、链接条目、炸弹、深层嵌套均被拒 |
| `INT-880` | 磁盘空间预检：不足时拒绝 |
| `INT-890` | 文本读写：多编码、原子替换、大小上限 |
| `INT-900` | **配置无损往返**：每种格式 ≥ 10 个真实样本（含中文注释、混合缩进、特殊转义），无修改保存后字节相同 |
| `INT-910` | **配置单键修改**：仅目标行变化，其余字节相同 |
| `INT-920` | 配置语法校验：非法内容被拒并给出行列 |
| `INT-930` | 配置备份：写入前产生备份；保留数量上限；可恢复 |
| `INT-940` | 配置识别与冲突标记 |
| `INT-950` | 模组扫描与元数据解析（真实样本 JAR） |
| `INT-960` | 模组启停、删除、上传 |
| `INT-970` | 运行中的文件锁：删除被禁用；停用失败返回 `file-locked` |
| `INT-980` | 长路径（> 260 字符）下的全部文件操作 |
| `INT-990` | 仅改大小写的重命名 |

安全测试：`SEC-140`（在文件端点的每个入口重新验证）、`SEC-150`、`SEC-160` … `SEC-164`、`SEC-170`、`SEC-180`、`SEC-181`、`SEC-190` … `SEC-193`。

## 16. 人工测试

无。

## 17. 调试启动方式

```powershell
dotnet run --project src/Servitium.Server -- --environment Development
dotnet run --project src/Servitium.Agent -- --environment Development
./eng/Test.ps1 -Category Integration
./eng/Test.ps1 -Category Security
```

## 18. 验收责任方

编码 Agent 自主验收。

## 19. 完成条件

1. 文件全部操作可用且受路径与配额约束；
2. 分片上传含断点续传可用，文件哈希一致；
3. 下载支持 `Range`；
4. URL 下载的 SSRF 防护通过全部负向测试；
5. 归档安全检查通过全部负向测试；
6. **四种配置格式的无损往返测试全部通过**（`INT-900`、`INT-910`）；
7. 配置语法校验、备份、原子保存生效；
8. 模组元数据解析覆盖七种来源；
9. 运行中的文件锁给出明确错误；
10. `INT-800` … `INT-990` 全部通过；
11. 本阶段列出的全部 `SEC-*` 通过；
12. `eng/Validate.ps1` 全量通过。

## 20. 禁止提前进入下一阶段的条件

* 任一配置格式的无损往返未通过（若某格式确实无法保证，必须按变更请求流程降级为「仅原始文本编辑」并获用户批准，而非静默接受有损往返）；
* 归档或路径安全测试存在未覆盖项；
* SSRF 防护存在绕过；
* 上传后文件哈希不一致。

## 21. 需要更新的动态记录

* `docs/records/current-status.md`
* `docs/records/development-log.md`
* `docs/records/decision-log.md`（记录配置保留结构解析等实现级选择）
* `docs/records/problem-log.md`
* `docs/records/validation-log.md`
* `docs/testing/requirements-traceability.md`
* `docs/testing/acceptance-matrix.md`
