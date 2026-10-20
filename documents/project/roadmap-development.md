# 开发路线

当前优先恢复可复验的核心工作流和可用 Desktop；不恢复旧按编号硬串行的 Gate/Profile。

1. 处理阻断的依赖安全问题，恢复 Client/Desktop 可构建环境。
2. 由外部评审给出界面设计，再按真实工作流实现专用 View/强类型 VM 并实际验收。
3. 在隔离场景完成服务、权限、真实配置/JAR、Minecraft、Prism/HMCL 与平台/容量验证。
4. Web 按用户决定继续暂停，恢复时采用根 web/ 的 Fluent React 技术栈。
5. 用户验收后另行决定交付；安装器与发布不自动启动。

旧 00～24 文件已过时的治理/阶段进度不再保留在源码树；需求定义、用户固定设计和有效验收场景仍保留。历史完成结论见[进度](status-development.md)，当前整理记录见[阶段 1](phase-1-repository_alignment.md)。
