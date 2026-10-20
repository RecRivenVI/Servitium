# 部署形态

当前没有安装器、正式版本、签名发布或 CI。Desktop csproj 仍是未打包 WinUI，Server/Agent 有 Windows 服务宿主代码；这不等于安装与服务验证完成。

用户确定的最终形态是完整安装包和远程 Agent 专用安装包，不提供 Portable，见[固定决策](../design/adr-1-product_baseline.md)。正式交付需在用户验收后另行明确要求。本轮不实现 MSI、MSIX、升级、服务安装或卸载流程。

安装目录/数据 ACL、服务恢复、升级备份与卸载数据保留等仍属[待办](../project/backlog-product.md)和[真实验证](../../validations/integration-backend/validation.md)。
