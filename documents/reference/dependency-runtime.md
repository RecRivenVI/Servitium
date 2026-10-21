# 依赖来源

NuGet 版本只在[Directory.Packages.props](../../Directory.Packages.props)定义，各项目锁文件固定解析结果；csproj 的 PackageReference 不写版本。

当前包：coverlet.collector, CommunityToolkit.Mvvm, CommunityToolkit.WinUI.Controls.SettingsControls, Google.Protobuf, Grpc.Core.Api, Grpc.AspNetCore, Grpc.Net.Client, Grpc.Tools, Konscious.Security.Cryptography.Argon2, Microsoft.AspNetCore.OpenApi, Microsoft.AspNetCore.Mvc.Testing, Microsoft.AspNetCore.SignalR.Client, Microsoft.EntityFrameworkCore.Design, Microsoft.EntityFrameworkCore.Sqlite, Microsoft.Extensions.Hosting, Microsoft.Extensions.Hosting.WindowsServices, Microsoft.NET.Test.Sdk, Microsoft.OpenApi, Microsoft.Windows.SDK.BuildTools, Microsoft.WindowsAppSDK, SQLitePCLRaw.lib.e_sqlite3, System.Security.Cryptography.ProtectedData, System.IdentityModel.Tokens.Jwt, Tomlyn, YamlDotNet, xunit, xunit.runner.visualstudio。

Desktop 终端/编辑器的 npm 依赖和 Node engines 见[package.json](../../src/Servitium.Desktop/WebAssetsSource/package.json)及锁文件。正式 Web 不存在，不虚构其依赖。

依赖安全修复与桌面首轮升级见[计划](../project/plan-desktop_round_one.md)。微软包和 ASP.NET Core 共享运行时采用安全补丁；Windows App SDK 采用[官方稳定通道](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)，构建工具同步。SettingsControls 提供 Fluent 的 SettingsCard/SettingsExpander，包采用 MIT 许可，见[官方包](https://www.nuget.org/packages/CommunityToolkit.WinUI.Controls.SettingsControls)。原生 DLL 由包管理还原，未复制第三方源码；运行输出不入库。

SDK 下载及 SHA-512 来自[微软下载](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)与官方 release-metadata。NuGet 审计保持开启，不放宽高危警告；为满足实际最低依赖同步 OpenAPI 与 SQLite 的集中锁定。未完成验证见[运行技术债](../project/debt-runtime.md)，源码许可与参考边界见[来源说明](upstream-references.md)。
