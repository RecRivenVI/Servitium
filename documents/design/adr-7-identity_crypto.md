# 使用 Konscious Argon2id 并独立实现 RFC 6238 TOTP

## 状态

已接受

## 背景

首次初始化、密码登录和可选 TOTP 需要可持久化、可验证且不依赖厂商在线服务的实现

## 决策

密码哈希使用 `Konscious.Security.Cryptography.Argon2` 1.3.1 并把参数编码进存储串；TOTP 使用平台 HMAC-SHA1 独立实现 RFC 6238，秘密经 DPAPI 机器范围保护

## 后果

选定包可明确设置 64 MiB、3 次迭代和并行度 2；TOTP 算法范围小且有 RFC 向量，平台 HMAC 与固定时间比较足够透明；DPAPI 避免数据库内明文秘密

原决策编号 DL-007；当前实现不足见技术债，规则与版本值不在此重复定义。
