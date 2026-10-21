# 开发环境

使用固定 SDK 和隔离的开发数据，工具版本不在本文重复定义。

| 组件 | 来源/用途 |
| --- | --- |
| .NET SDK | [global.json](../../global.json)，可从[微软官方](https://dotnet.microsoft.com/download/dotnet/10.0)取得对应免安装包，在命令中显式使用 |
| Windows App SDK/WinUI | [中央依赖](../../Directory.Packages.props)；构建需要 Windows SDK，运行需要匹配的 x64 Windows App Runtime |
| WebView2 | Evergreen Runtime，仅用于 Desktop 本地终端/编辑器 |
| Node/npm | [Desktop 资源包](../../src/Servitium.Desktop/WebAssetsSource/package.json)和锁文件；正式 Web 未开始 |
| Java/JDK | 当前合成夹具需要 javac、jar、java，现有测试从 PATH 查找；系统 Java 的配置由用户管理 |
| PowerShell/Git | PowerShell 7，Git；本轮不创建 CI |

复制 tools/build/Local.props.example 为被忽略的 Local.props，字段见[配置](../reference/configuration-development.md)。SDK 放在工作区 Scratches/Toolchains，不安装到系统、不改系统 PATH/环境变量/注册表。

源码默认可用标准 dotnet 命令；本项目工具通过 Local.props 将构建、运行与结果放在工作区 Artifacts。没有本机配置时不运行工具，不猜测正式数据目录。

当前环境与实机验证缺口见[技术债](../project/debt-runtime.md)。缺少管理员权限、第二平台或真实样本时记录无法比较，不安装服务来绕过边界。WinUI 实际运行使用构建出的原生 apphost；仅给该子进程指定本机 SDK 运行时位置，避免托管入口改变应用资源定位，不修改系统配置。
