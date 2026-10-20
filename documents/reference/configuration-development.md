# 本机开发配置

本机路径只定义在被忽略的 tools/build/Local.props；模板不提供任何个人路径。它由 MSBuild 与工具共同读取，不更改系统 PATH、环境变量或注册表。

| 字段 | 默认/含义 |
| --- | --- |
| ServitiumDotnetExe | 模板为 dotnet；可填固定 SDK 免安装包内的 dotnet.exe；实际版本仍以 global.json 为准 |
| ServitiumArtifactsRoot | 无默认，必填仓库外的工作区 Artifacts/Servitium 目录 |
| ReferencesHmcl、ReferencesMcsmanager、ReferencesPrism、ReferencesPackPrototype | 需要实地观察时再按工作区台账定位填写；缺省不访问，不沿用旧位置 |

ServitiumArtifactsRoot 下按 Build、Runtime、Validation、Records 分开保存产物。工具只读取当前任务所需字段。生成物与记录可留存，清理时遵守工作区回收站规则。

生产默认路径/网络参数来自 ServerHostOptions、AgentHostOptions 与 appsettings 的选项绑定，当前仍有重复定义待收敛，见[事实来源债](../project/debt-rules.md)。本轮不改变监听、身份或数据库行为；私密配置用 user-secrets 或被忽略的本地配置，不写入此文件。
