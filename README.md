# WeChat-Notifier

WinUI 3 微信通知工具：简体中文界面、液态玻璃气泡或系统通知、单文件裁剪发布。

## 软件截图

![WeChat-Notifier 设置界面](docs/images/settings-appearance.png)

![WeChat-Notifier 液态玻璃演示气泡](docs/images/demo-bubble.png)

## 运行与配置

- `Config/config.json`：便携压缩包位于真实 EXE 所在目录；MSIX 位于 `%LOCALAPPDATA%\Packages\<包族名>\LocalState\Config\config.json`，首次启动生成。
- `Log/app.log`：Serilog 日志位于同一数据根目录的 `Log` 文件夹；单个日志文件达到 2 MiB 时滚动，最多保留 14 个文件。
- 关闭设置窗口只隐藏到托盘；托盘右键「退出」才停止程序。
- 双击托盘图标调用已配置的微信快捷键打开微信；右键「设置…」打开 WeChat-Notifier 主界面。
- 「不再提示」立即保存，不会顺带保存设置窗口中尚未提交的其他修改。
- 微信在前台时默认不通知；后台、最小化和托盘状态正常通知。全屏时只写入通知中心。

## 通知模式

- 正常模式使用绿色托盘图标；隐私模式为蓝色 `#4E6EF2`，静默模式为黑色 `#28282B`。新消息会显示未读角标，角标最多显示 `99+`；正常和隐私模式还会闪烁，静默模式不闪烁。
- 隐私模式的气泡和 Windows 横幅只显示“收到了一条消息”，通知中心保留详细内容。静默模式不显示气泡或 Windows 横幅，通知中心仍保留详细内容。
- 默认同时按住 `Ctrl`、`Alt`、`,`、`.` 四个键（`Ctrl+Alt+,+.`），在正常、隐私、静默模式间循环切换，也可右键托盘，在模式二级菜单选择；一级菜单标题会显示当前模式。切换提示显示为不超过 1.5 秒的本地气泡，不写入 Windows 通知中心。快捷键设置支持点击输入框录制组合键，标点键松开后确认；Esc 取消，Backspace 清除并停用；保存后生效。已有自定义快捷键配置保持原值，可通过录制或恢复默认设置采用新组合。
- 行为设置可将 EXE 加入隐私或静默名单。程序约每秒检测名单中的运行程序；静默模式优先于隐私模式，名单程序退出后恢复手动设置的模式。自动模式切换提示可以关闭；同一个 EXE 从一份名单移到另一份时会要求确认。修改保存后生效。

单文件的临时解包目录只用于依赖和图标，不用于配置、日志。便携版运行目录不可写时会报错，不回退用户目录。旧用户目录的配置不会自动覆盖新配置，需要时自行复制到 `Config/config.json`。

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

推送 `vMAJOR.MINOR.PATCH` tag 会触发 GitHub Actions，仅构建 Windows AOT x64 与 ARM64，并发布两个压缩包和一个包含两种架构的 MSIX bundle。MSIX 使用每次发布新生成的自签名证书；安装前需将随包发布的 `.cer` 导入“本地计算机\受信任的人”证书存储（需要管理员权限），每次更新都要先信任对应版本的证书：

```powershell
Import-Certificate -FilePath .\WeChat-Notifier-signing.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

自测入口为 `WeChat-Notifier.exe --selftest`，界面自动验证入口为 `--ui-smoke`。`--mode-smoke` 使用实际 EXE、热键和系统通知验证模式切换，并将结果写入 `Log/mode-smoke`。构建脚本不会把已有的 `Config/Log` 内容打进单文件压缩包。

数据库读取已采用文件事件唤醒：`.db/-wal/-shm` 变化防抖 125 ms，连续写入最多等待 1 秒；默认每 3 秒核对提交状态，未发生新提交时不打开 SQLite。同一 WAL 代次只增量解密新增提交页，代次切换或 checkpoint 自动回退全量快照。联系人和企业信息按各自数据库的提交版本缓存。

原配置中的 `PollIntervalMs` 继续有效，但现在表示轻量提交头核对间隔，不再表示固定查询间隔。`--probe-wechat` 可在 12 秒内只读检测当前数据库并输出查询次数、CPU 时间与事件触发次数，不发送通知。
