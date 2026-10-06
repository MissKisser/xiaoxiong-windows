# 小熊模拟器 · Windows 端

在 Windows 上批量运行安卓实例的模拟器。本仓库是**小熊模拟器**项目的 Windows 端实现。

共享规格层：[xiaoxiong-emulator-spec](https://github.com/MissKisser/xiaoxiong-emulator-spec)（以 submodule 引入至 `spec/`）

## 技术路线

以 QEMU 承载安卓系统镜像，通过 **WHPX**（Windows Hypervisor Platform）调用宿主硬件虚拟化能力，获得接近原生的运行性能。每个实例是独立的虚拟机，拥有自己的虚拟硬件配置与磁盘镜像。

宿主上确实存在虚拟化痕迹，因此隐藏工作是**对抗性**的。

## 目标能力

| 能力 | 要求 |
|---|---|
| 可获取 Root 权限 | 实例内可获得 Root 权限，重启后保持 |
| 系统可写 | 系统分区可写，可刷模块、可改系统 |
| 可刷镜像 | 支持替换实例内的安卓镜像 |
| 可运行 ARM 应用 | 通过 native bridge 运行 ARM 原生应用 |
| 设备标识独立 | 序列号 / Android ID / IMEI 每实例强制独立 |
| 网络隔离 | 默认仅回环暴露，对外暴露需阻断式确认 |

## 性能基线

以 32 GB 内存宿主为基准：

| 指标 | 目标 |
|---|---|
| 冷启动至桌面 | ≤ 60 s |
| 实例内应用冷启动 | ≤ 3 s |
| 交互延迟 | ≤ 150 ms |
| 稳态帧率 | ≥ 30 FPS |
| 单实例内存占用 | ≤ 4 GB |
| 多开 | ≥ 3 实例并行，延迟劣化 ≤ 30% |

## 运行时约束

- 宿主需启用 **Hypervisor Platform** 可选功能
- 通过 `spec/` 中的契约定义实例配置、镜像清单与统一术语
- 本仓库代码中**不得**出现字面量颜色值，须引用 `spec/tokens/design-tokens.json`

## 当前状态

核心层与界面层已实现，**336 个自动化测试全绿**（核心层 300 + 界面层 36），并通过**全新递归克隆的端到端复现**验证。

已实现的子系统：QEMU 参数生成与进程托管、QMP 客户端、ADB 客户端（含 sync 子协议）、实例生命周期与配置仓库、设备标识隔离、输入通道探测、规格校验与双向兼容测试、WPF 界面外壳。

**尚未验证的部分**：镜像能否启动到桌面。这是判定本路线价值成立与否的前提，镜像仍在下载中。

首验镜像为 **Bliss OS 14.10.3（Android 11）**。选型依据见项目状态文档：Bliss OS 17 与 18 已从官方渠道下架，Android-x86 官方最新稳定版仍是 9.0-r2（Android 9），无法满足主流应用对 Android 10 的最低要求。

## 构建

需要 .NET 8 SDK。契约以 submodule 形式位于 `spec/`，克隆时须一并拉取：

```bash
git clone --recurse-submodules https://github.com/MissKisser/xiaoxiong-windows.git
```

构建与测试：

```bash
dotnet build XBear.sln -c Release
dotnet test XBear.sln -c Release --no-build
```

工程开启了 `TreatWarningsAsErrors` 与 `GenerateDocumentationFile`，**零警告是硬要求**：`public` 与 `protected` 成员必须携带完整 XML 文档标签，否则编译失败。

界面运行时依赖宿主已安装 **QEMU**（默认在 `C:\Program Files\qemu\`）与已启用的 **Hypervisor Platform** 可选功能。

## 许可证

本项目采用 **Apache License 2.0**，完整条款见 `LICENSE`。

QEMU 以独立进程方式调用，不构成链接，其 GPLv2 不影响本项目授权。scrcpy 为 Apache-2.0。