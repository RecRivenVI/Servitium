# 未打包 Desktop 本地化使用 ResourceLoader 优先与 JSON 回退

## 状态

已接受

## 背景

`WindowsPackageType=None` 的 WinUI 进程可能在资源加载器构造或读取时返回 `FileNotFoundException`/`NamedResource`，不能让托盘和主窗口启动失败

## 决策

Desktop 优先使用 Windows App SDK `ResourceLoader`；当未打包宿主没有默认资源上下文时，按当前语言读取 Contracts 输出的 `Localization/{language}.json`，未知键返回稳定键名

## 后果

保持正式 PRI 资源优先，同时复用已经由 `Generate.ps1` 校验的共享双语资源；稳定键回退便于诊断且不伪造文案

原决策编号 DL-015；当前实现不足见技术债，规则与版本值不在此重复定义。
