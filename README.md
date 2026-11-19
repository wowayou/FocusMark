# FocusMark

<img src="assets/focusmark.svg" width="48" height="48" alt="FocusMark 薄荷绿书签图标">

工作被打断前，按 **Ctrl+Alt+J**，写下回来要做的第一件事，再按 **Enter**。回来时点击屏幕顶部的胶囊，选择 **返回原窗口**。

## 运行

在 Windows 11 x64 上，[下载 FocusMark.exe](https://github.com/wowayou/FocusMark/raw/refs/heads/main/dist/FocusMark.exe) 后双击运行。需要系统组件 .NET Framework 4.8.1。本次交付是未签名的单文件桌面程序。首次启动没有备注时，只显示系统托盘图标，可能位于“显示隐藏的图标”中。

| 操作 | 结果 |
|---|---|
| 在工作窗口按 Ctrl+Alt+J | 新建时记录当时的前台窗口，编辑已有备注时保留原关联 |
| 输入 1–200 字，按 Enter 或点保存 | 保存成功后显示单行胶囊。计数以 Unicode 码点为单位，换行保存为空格 |
| 在另一个工作窗口按 Ctrl+Alt+J，勾选「将返回目标改为刚才的窗口」并保存 | 下次点击返回会切到这个窗口。重启后也可以用此方式重新关联 |
| 按 Esc | 放弃本轮文字和窗口关联的更改 |
| 点击胶囊 | 显示全文，可返回原窗口、编辑或完成。返回后备注继续保留 |
| 点击完成 | 删除当前备注并隐藏胶囊。下次用快捷键可关联新的工作窗口 |
| 中键点击胶囊，或在托盘点隐藏 | 暂时收起界面。快捷键或托盘“打开”可再编辑 |
| 托盘 → 退出 | 关闭程序。下次启动时恢复已保存的文字 |

本机 `Ctrl+Alt+M` 已被占用，因此选择 `Ctrl+Alt+J`。若这组键也有冲突，启动时会提示，托盘仍可打开编辑框。从托盘新建只记录文字，请用全局快捷键记录工作窗口。

## 数据与窗口关联

当前备注以明文存放在 `%LOCALAPPDATA%\FocusMark\note.json`。保存时先在同目录写入并刷新 `note.json.tmp`，再替换正式文件。中断后以上一次成功提交的正式文件为准。遇到无法解析或内容无效的文件，程序会保留为 `note.json.damaged-…` 并提示路径，可用文本编辑器查看。遇到访问失败会报错。备份时请复制正式文件。

窗口句柄（HWND，Windows 用于标识窗口的编号）、进程及线程编号、进程创建时间和临时窗口标记只用于当前会话。程序关闭后关联失效。只有能验证身份的窗口才可返回，Windows 拒绝前台切换时会提示手动返回。权限较高的窗口可能无法关联。只恢复到应用窗口，不定位文档、浏览器标签页或光标。

程序离线运行，无账号、网络请求、遥测或自动启动。全屏时可用中键或托盘隐藏胶囊。独占全屏、跨权限窗口、150% 缩放及显示器热插拔的验证状态见 [ACCEPTANCE.md](ACCEPTANCE.md)。

## 从源码构建

在 Windows PowerShell 中进入本目录运行下列命令。`build.ps1` 使用本机 Framework 的 x64 C# 编译器，嵌入 WPF 布局和 DPI 清单，不需要下载包。本机可用的现代 .NET SDK 仅有 3.1 和 5.0，均已停止支持，因此项目选择随 Windows 维护的 .NET Framework 4.8.1。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
.\dist\FocusMark.exe
```

`ExecutionPolicy Bypass` 仅适用于这次 PowerShell 进程。可选 `-Test` 编译并运行数据及窗口身份测试，测试程序在 `build\` 下。`FocusMark.App\` 是应用源码，`FocusMark.Tests\` 包含测试及桌面验收脚本。桌面脚本需要解锁的桌面、VS Code 和 Edge，会移动焦点及鼠标。运行命令和本次实际结果见 [ACCEPTANCE.md](ACCEPTANCE.md)。
