# HMCL 内容使用 SHA-1 CAS 与 SQLite 原子 Channel 指针

## 状态

已接受

## 背景

HMCL manifest 的强 ETag 和文件哈希均以 SHA-1 为可观察契约，同时要求 Release 不可变和 Channel 原子切换

## 决策

Release 文件按 SHA-1 内容寻址存放，元数据与引用计数进入 SQLite；发布前逐项重哈希，在单一写事务中切换 Channel 当前 Release

## 后果

CAS 避免重复内容，流式物化控制内存，事务指针切换避免客户端观察到半发布状态；公开与私有 Channel 共用不可变 Release 和原子指针语义，私有分发另由令牌边界保护

原决策编号 DL-009；当前实现不足见技术债，规则与版本值不在此重复定义。
