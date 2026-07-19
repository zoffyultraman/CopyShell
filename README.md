# CopyShell

CopyShell 是一个基于 Windows 原生 `robocopy.exe` 的资源管理器增强复制工具。

它不会替换 Windows 自带的复制、粘贴或拖放行为，而是在资源管理器右键菜单中提供更适合大文件、批量文件和可靠同步场景的高级操作：

- **高级复制到…**
- **高级移动到…**
- **同步到…**

> 当前状态：项目处于架构设计和目录搭建阶段，尚未提供可运行版本。

## 目标平台

- Windows 10 1809+
- Windows 11
- x64
- 当前用户安装
- 无需管理员权限

## 设计目标

- 使用 Windows 自带的 `robocopy.exe` 执行文件操作，不嵌入或修改系统组件。
- Shell Extension 保持极薄，避免影响 `explorer.exe` 的稳定性。
- 使用 C# 与 WinUI 3 提供现代任务界面。
- 将任务模型、安全策略和复制引擎分离。
- 支持长期演进为具有队列、恢复、历史和多引擎能力的专业复制工具。

## 计划功能

### 高级复制

- 复制文件和目录。
- 保留数据、属性和时间戳。
- 支持多线程、失败重试和断点续传。
- 支持长路径、UNC 路径和批量选择。

### 高级移动

- 使用 Robocopy 完成可靠移动。
- 复制成功后再删除源项目。
- 正确展示部分完成和失败状态。

### 镜像同步

- 使用 Robocopy `/MIR` 镜像单个源文件夹。
- 执行前展示源目录、目标目录和风险信息。
- 对根目录、系统目录和其他高风险目标进行保护。
- 所有删除型操作必须二次确认。

### 后续能力

- 持久化任务队列。
- 暂停、停止与恢复。
- 任务历史和失败重试。
- 文件大小、时间或哈希校验。
- ACL、所有者和审核信息复制。
- 带宽限制和按磁盘调度。
- FastCopy 等可选复制引擎。
- Windows 11 第一层现代右键菜单。

## 架构概览

```text
Windows Explorer
       │
       ▼
CopyShell.ShellExtension
       │  一次性版本化 JSON 请求
       ▼
CopyShell.App
       │
       ▼
CopyShell.Core
       │
       ▼
CopyShell.Robocopy
       │
       ▼
robocopy.exe
```

### CopyShell.ShellExtension

原生 C++ COM Shell Extension，MVP 使用经典 `IContextMenu`。

它只负责：

- 从 `IDataObject/CF_HDROP` 获取资源管理器选择项。
- 创建一次性 UTF-8 JSON 请求。
- 启动 `CopyShell.App.exe`。

Shell Extension 不会在 Explorer 中加载 CLR、.NET、WinUI，也不会执行文件复制逻辑。

### CopyShell.App

C# + WinUI 3 桌面应用，负责：

- 读取 Shell 请求。
- 选择目标路径。
- 编辑任务选项。
- 展示危险操作确认、进度和日志。
- 将任务提交给 Core。

### CopyShell.Core

引擎无关的业务层，负责：

- Task Model 和任务状态。
- 源路径与目标路径校验。
- 风险评估。
- 生成逻辑复制计划。
- 定义复制引擎和持久化接口。
- 统一解释执行结果。

### CopyShell.Robocopy

Robocopy 引擎适配器，负责：

- 将逻辑计划转换为 Robocopy 参数。
- 使用独立子进程执行 `robocopy.exe`。
- 管理 Unicode 日志。
- 处理退出码、取消和进程树终止。

Robocopy 返回码 `0–7` 表示成功或非致命差异，`8+` 才表示失败。

## 项目结构

```text
CopyShell/
├─ .github/
│  └─ workflows/
│     └─ build.yml
├─ installer/
│  ├─ CopyShell.Identity/
│  └─ CopyShell.Installer/
├─ schemas/
├─ src/
│  ├─ CopyShell.App/
│  ├─ CopyShell.Core/
│  ├─ CopyShell.Robocopy/
│  └─ CopyShell.ShellExtension/
├─ tests/
│  ├─ CopyShell.Core.Tests/
│  ├─ CopyShell.IntegrationTests/
│  ├─ CopyShell.Protocol.Tests/
│  └─ CopyShell.Robocopy.Tests/
├─ LICENSE
└─ README.md
```

## 构建

项目计划仅通过 GitHub Actions 进行正式构建，不要求贡献者在当前目录中完成本地 Windows 构建。

工作流位于：

```text
.github/workflows/build.yml
```

加入 `CopyShell.sln` 和项目文件后，工作流将自动：

1. 在 Windows Server 2022 x64 环境中恢复依赖。
2. 使用 .NET 8 和 MSBuild 构建 Release 版本。
3. 运行托管测试项目。
4. 发布 WinUI 3 应用。
5. 构建并收集原生 Shell Extension。
6. 上传 `win-x64` 构建产物。

当前仓库只有项目骨架时，工作流会跳过实际构建。

## Windows 右键菜单

MVP 使用经典 `IContextMenu`：

- Windows 10 中显示在经典右键菜单。
- Windows 11 中通常显示在“显示更多选项”。

后续计划通过 MSIX 包身份和原生 `IExplorerCommand` 支持 Windows 11 第一层现代菜单。两种菜单实现将复用相同的请求协议、应用程序和业务层。

## 安全原则

- 不 Hook、不注入、不替换 Windows Explorer。
- 不修改或替换系统 `robocopy.exe`。
- 不在 Shell Extension 中执行耗时任务。
- 不拼接可执行命令字符串。
- 不允许目标位于源目录内部。
- 同步只允许一个源文件夹。
- `/MIR` 必须经过风险检查和二次确认。
- 取消任务时终止完整 Robocopy 进程树。

## 参与项目

项目尚处于早期阶段。提交实现前，请保持以下边界：

- Shell Extension 必须是极薄的原生组件。
- UI 不直接构造 Robocopy 命令。
- Core 不依赖 WinUI、COM 或具体复制引擎。
- Robocopy 特有逻辑只进入 `CopyShell.Robocopy`。
- 新功能应同时补充相应测试。

## License

CopyShell 使用 [GNU General Public License v3.0](LICENSE)。
