# Desktop 技术债

PB-039 仍为阻断，当前界面等待外部评审重设计；本轮只移动资源目录与命名空间，不改布局、文案或业务交互。

| 规则/位置 | 影响 | 建议 |
| --- | --- | --- |
| U-02/U-06/U-07/U-08；MainWindow.xaml.cs、ContractPage.cs | 真实截图曾有侧栏/标题叠印、输入宽度、空白与主动作层级问题；有限 UIA 不覆盖所有文字 | 按外部设计逐流程验收；PB-039 未关闭 |
| DN-09、C-07；Views/ContractPage.cs、ProductPages.cs、ViewModels/PageActionDispatcher*.cs | 30 页 C# 动态构造/字符串分派，仅局部 ObservableObject；难独立单测与维护 | 专用 XAML View、强类型页面 VM/命令，保留 UI 语义契约 |
| DN-10/U-01；ContractPage、MainWindow、DashboardEmbedHost | 硬编码尺寸、统一纵向卡片与通用列表不适配所有工作流 | 采用 WinUI 标准主题/间距和真实信息层级 |
| U-03/U-04；页面快照、错误与本地化回退 | 仍需验证协议值、加载/空/离线/权限/错误的用户表达 | 外部设计后逐场景实测，不仅检查字符串存在 |
| T-03/U-08；DesktopFoundationTests 与旧 UIA | 150% DPI 三宽度及 8 顶层页面不代表 30 页；100/125/200%、读屏/高对比度/键盘待测 | 使用 validations/ui-clients，不用旧截图宣称新 UI 完成 |
| C-06；App.xaml.cs / DesktopSelfTestOptions | self-test/isolated-test/ui-page-test 在生产入口；最后页面开关未完整复验 | 后续集中开发入口；本轮保留测试可重复性，不改运行语义 |

事件流 PB-040 的 CSRF/空 GUID 修复有历史局部证据，但修复后的全新密码登录和全部当前 Desktop 仍需重跑。资源移动不构成新视觉验收。
