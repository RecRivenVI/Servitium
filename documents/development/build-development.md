# 构建

产品行为与固定依赖保持不变；公共设置在[Directory.Build.props](../../Directory.Build.props)，SDK 在[global.json](../../global.json)。

```powershell
. ./tools/build/Development.ps1
$context = Get-DevelopmentContext
& $context.Dotnet restore Servitium.sln --locked-mode --disable-parallel --verbosity minimal
& $context.Dotnet build Servitium.sln --configuration Release --no-restore --maxcpucount:1
```

这是整理前后比较的同一入口。实际命令以本机配置解析 SDK；日志、退出码与测试结果放在仓库外，当前结论见[进度](../project/status-development.md)。本轮未升级有问题的依赖。

## 输出

Local.props 的 ServitiumArtifactsRoot 下，Build/bin 与 Build/obj 按项目隔离，Build/Fixtures 保存合成 Java JAR，Build/OpenApi 保存 OpenAPI。测试由 MSBuild 生成源码根元数据定位当前文档，并将 JAR 复制到测试输出；不从外部构建目录向上猜测仓库。

Desktop 的本地资源构建接收 MSBuild 提供的外部输出路径。依赖以 package-lock.json 还原，资源源码保留在 WebAssetsSource，不代表正式 Web 已实现。

EF 设计时工厂通过工具参数接收外部设计数据库目录；不再按程序集所在层数拼仓库内 artifacts。它不读取正式运行数据库。
