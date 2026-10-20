# 依赖来源

NuGet 版本只在[Directory.Packages.props](../../Directory.Packages.props)定义，各项目锁文件固定解析结果；csproj 的 PackageReference 不写版本。

当前包：coverlet.collector, CommunityToolkit.Mvvm, Google.Protobuf, Grpc.Core.Api, Grpc.AspNetCore, Grpc.Net.Client, Grpc.Tools, Konscious.Security.Cryptography.Argon2, Microsoft.AspNetCore.OpenApi, Microsoft.AspNetCore.Mvc.Testing, Microsoft.AspNetCore.SignalR.Client, Microsoft.EntityFrameworkCore.Design, Microsoft.EntityFrameworkCore.Sqlite, Microsoft.Extensions.Hosting, Microsoft.Extensions.Hosting.WindowsServices, Microsoft.NET.Test.Sdk, Microsoft.OpenApi, Microsoft.Windows.SDK.BuildTools, Microsoft.WindowsAppSDK, SQLitePCLRaw.lib.e_sqlite3, System.Security.Cryptography.ProtectedData, System.IdentityModel.Tokens.Jwt, Tomlyn, YamlDotNet, xunit, xunit.runner.visualstudio。

Desktop 终端/编辑器的 npm 依赖和 Node engines 见[package.json](../../src/Servitium.Desktop/WebAssetsSource/package.json)及锁文件。正式 Web 不存在，不虚构其依赖。

本轮未新增或升级产品依赖。已知公告和未完成验证见[运行技术债](../project/debt-runtime.md)，源码许可与参考边界见[来源说明](upstream-references.md)。
