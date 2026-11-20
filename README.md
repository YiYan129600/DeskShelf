# 桌屿 · DeskShelf

桌屿是一款 Windows 桌面整理工具。它把文件和快捷方式放进可折叠的桌面分区。接上第二块屏幕，两边都能打开同一份文件、拖动分区和调整大小，各块屏幕仍可使用自己的壁纸。

## 下载和启动

1. **[下载完整 Windows x64 版](https://github.com/YiYan129600/DeskShelf/archive/refs/heads/main.zip)**，解压到一个可长期保留、当前用户有写入权限的文件夹。
2. 双击根目录的 **桌屿.exe** 启动桌面分区。请保留同目录的「分区」文件夹，程序会从中加载引擎和规则。
3. 运行中再次双击 **桌屿.exe** 可打开设置。需要在登录时自动启动，可以勾选「随 Windows 启动」。

支持 Windows 10 1809 及以上或 Windows 11（x64）。启动器和归档工具使用 .NET Framework 4.8，设置窗口使用 Microsoft Edge WebView2 Runtime。

如果已从其他目录运行桌屿或 PecoFence，先从通知区域退出旧实例，再打开新目录里的「桌屿.exe」。布局和规则保存在各自目录的 `分区/PecoFence/config/` 下。

## 桌面上的操作

| 想做什么 | 怎么操作 |
| --- | --- |
| 整理文件 | 在桌面空白处画一个矩形，选择「新建栅栏」，把文件拖进分区。默认分区包含工具入口、游戏、项目资料、图片影音、收件箱和最近 7 天。 |
| 移动、缩放和折叠 | 拖标题移动，拖边缘或角调整大小，双击标题折叠或展开。右键可锁定或解锁布局。 |
| 双屏使用同一批文件 | 在 Windows 的「扩展」显示模式下，两屏都可以拖动和缩放。松开鼠标后，另一屏按自己的可用空间调整。把文件拖到另一分区，各屏归类也会更新。镜像开关在「布局与备份 → 多显示器」。 |
| 自动排列 | 右键分区，选「自动排列 → 对齐所有桌面分区」，也可以只按名称排列区内图标。 |
| 展示壁纸 | 双击空白桌面，各屏分区和任务栏会一起隐藏。再双击空白处恢复。壁纸在 Windows 或 Wallpaper Engine 中按显示器分别设置。 |
| 调整颜色和透明度 | 右键分区，选择「颜色」或「透明度」。默认跟随 Windows 强调色，手动选色后，下次系统强调色或明暗主题变化时重新跟随。 |
| 只在鼠标经过时显示 | 在分区右键菜单勾选「仅鼠标悬停时显示此分区」，鼠标移回该区域即可查看和操作。 |

分区记录图标归属，文件保留在原路径。自动归类会检查扩展名、文件名和快捷方式目标。例如，带「合同」或「报价」的文件进入项目资料，Steam 游戏进入游戏区，Wallpaper Engine 和 Blender 的 Steam 快捷方式进入工具入口。

「最近 7 天」每分钟读取个人桌面顶层，点文档名即可打开。需要把文件实际移动到分类文件夹时，打开根目录的 **文件归档.exe**，先预览，再点「移动」。工具提供撤销归档。

要在 Windows 桌面空白处的右键菜单中加入「自动排列桌面分区」，从仓库根目录执行以下命令。Windows 11 的入口位于「显示更多选项」。用同一命令加上 `-Remove` 可移除入口。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\分区\桌面右键菜单.ps1"
```

## 源码与构建

| 组件 | 源码 |
| --- | --- |
| 桌屿启动器、WPF 文件归档工具 | [src](src/) |
| 初始分区、分类规则、最近文档和壁纸模式助手 | [分区](分区/) |
| 桌面分区引擎 | [桌屿对 PecoFence 的源码补丁](分区/PecoFence-桌屿.patch) · [引擎构建说明](分区/PecoFence/桌屿版本.txt) |

在 Windows 上运行 `.\src\build.ps1` 可重新编译启动器、归档工具和壁纸助手。Rust 引擎以 PecoFence v0.4.0 为基线，固定提交为 `76fe196569b7e4f7c837f315e65fbf1470a68790`。[上游源代码](https://github.com/DayuanJiang/PecoFence/tree/76fe196569b7e4f7c837f315e65fbf1470a68790)

PecoFence 引擎沿用 Apache-2.0 许可证。WebView2 Loader 和其他第三方组件的许可说明随软件提供。文件归档工具的分组浏览参考了 DeskBox，归档预览参考了 organize。[引擎许可证](分区/PecoFence/LICENSE) · [署名](分区/PecoFence/NOTICE) · [第三方许可](分区/PecoFence/THIRD-PARTY-LICENSES.txt) · [WebView2 Loader 许可](分区/PecoFence/LICENSE-WebView2Loader.txt) · [DeskBox](https://github.com/Tianyu199509/DeskBox) · [organize](https://github.com/tfeldmann/organize)
