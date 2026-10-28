# WeChat-Notifier

WinUI 3 微信通知工具：简体中文界面、液态玻璃气泡或系统通知、单文件裁剪发布。

## 运行与配置

- `Config/config.json`：位于真实 EXE 所在目录，首次启动生成。
- `Log/app.log`：位于真实 EXE 所在目录。
- 关闭设置窗口只隐藏到托盘；托盘右键「退出」才停止程序。
- 「不再提示」立即保存，不会顺带保存设置窗口中尚未提交的其他修改。
- 微信在前台时默认不通知；后台、最小化和托盘状态正常通知。全屏时只写入通知中心。

单文件的临时解包目录只用于依赖和图标，不用于配置、日志。运行目录不可写时会报错，不回退用户目录。旧用户目录的配置不会自动覆盖新配置，需要时自行复制到 `Config/config.json`。

## 项目结构

```text
src/WeChat-Notifier.App/
├─ App.xaml / Program.cs      启动与组装
├─ Configuration/             配置模型、JSON 与运行路径
├─ Domain/                    通知内容、会话与窗口状态
├─ Application/               应用协调与通知规则
├─ Infrastructure/
│  ├─ WeChat/                 微信状态、数据库读取
│  ├─ Windows/                托盘、热键、Win32 互操作
│  ├─ Notifications/          Windows 通知适配
│  └─ Logging/                日志
├─ Views/                     设置页、控件与气泡渲染
├─ ViewModels/                设置工作副本与编译绑定
├─ Diagnostics/               自测与诊断入口
└─ Build/                     简中 MUI、PRI 裁剪
```

保留一个桌面项目，按职责划分目录和命名空间，不增加 Web 控制器、通用仓储、额外 DI 层或进程间通信。

## 构建

使用 PowerShell 7 和 .NET 10 SDK：

```powershell
./build.ps1                       # 简中、自包含、PublishTrimmed、单 EXE
./build.ps1 -Architecture arm64
./build.ps1 -Aot                  # 原生 AOT；WinUI 依赖仍为多文件
```

自测入口为 `WeChat-Notifier.exe --selftest`，界面自动验证入口为 `--ui-smoke`。构建脚本不会把已有的 `Config/Log` 内容打进单文件压缩包。

数据库触发优化方案见 [devdocs/database-monitoring.md](devdocs/database-monitoring.md)。本轮只实现微信窗口状态抑制，尚未将数据库轮询替换为文件事件监听。
