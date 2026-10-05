# osu! Replay Practice

为 osu!lazer 增加回放接管训练：观看回放 → 定位 → 接管 → 倒计时 → 手动练习，反复练习同一段。

**这是基于 [ppy/osu](https://github.com/ppy/osu) 的非官方实验分支，不是 osu! 官方发行版。** 当前仅支持 **osu!standard**，已支持真实回放和 **Autoplay（AT）** 自动基线。

## 下载测试版

从 [Releases](https://github.com/post7794/osu-replay-practice/releases) 下载 Windows x64 测试 ZIP，**完整解压**后运行 `Start-Replay-Practice.cmd` 或 `osu!.exe`。无需安装 .NET SDK 或运行时。

- 默认使用独立数据目录 `%APPDATA%\osu-replay-practice-test`，关闭自动更新和文件关联。
- 不包含开发者的曲目、回放、账号配置或数据库；请自行导入测试素材。
- 不要把测试版的存储位置设置为日常使用的 osu! 数据目录。
- 测试接管不需要登录账号。**只有接管训练禁止保存和提交；普通游戏、登录及导入仍按原流程工作，整个客户端不是强制离线模式。**

## 开始练习

### 有真实回放

导入匹配的谱面与回放，进入回放观看，定位后按 `Ctrl+Enter` 或点击接管按钮。3 秒倒计时期间谱面冻结，结束后仅使用真实输入。

### 没有回放：Autoplay

选歌界面按 `Ctrl+Enter` 启动 Autoplay（或选择 AT 后开始），定位到目标片段，再按 `Ctrl+Enter` 接管。

**自动基线不会找回中途退出那局的真实操作、失误或血量。** 自动输入只用于恢复历史，手动阶段移除 AT；重试复用同一基线，不重新随机谱面。CN/RX/AP/SO、未知 Mods 和非 standard 仍不支持接管。

## 功能与默认快捷键

| 操作 | 默认快捷键 |
| --- | --- |
| 接管 | `Ctrl+Enter` |
| 暂停 / 继续 | `Space`、鼠标中键 |
| 重试相同起点 | `Ctrl+Shift+Enter` |
| 返回回放，在练习起点暂停 | `Ctrl+Backspace` |
| 直接退出 | `Ctrl+Shift+Backspace` |
| 上一个 / 下一个物件（刚出现时） | `A` / `D` |
| 后退 / 前进一秒 | `Q` / `E` |
| 上一个 / 下一个原回放 Miss | `Shift+N` / `N` |
| 上一个 / 下一个少加 combo、但不断连的判定 | `Shift+M` / `M` |
| 加速 / 减速；重置速度 | `W` / `S`；`F` |
| 后退 / 前进一回放帧 | `,` / `.` |

快捷键可在回放内和游戏官方按键配置的 Replay 分组中修改。
### 定位当前物件

点击“上一物件 / 下一物件”（默认 A / D）后，暂停画面会出现**金色四角定位框和“当前 #编号”**。
编号是谱面物件的顺序，不是皮肤上的连击编号；滑条标记头部，转盘标记中心，堆叠物件按实际显示位置定位。
物件重叠时，在侧栏**按住“聚焦当前物件”按钮**可暂时淡化其他物件；松开或移开鼠标即恢复。
定位层不读取或替换皮肤贴图，不修改回放时间、判定或成绩。恢复播放、跳到其他时间或开始正式练习时自动隐藏。
支持滑条/转盘中途精确接管、物件范围练习、训练中定位、可拖拽缩放并吸附到谱面外侧的菜单。
Miss、空血不打断训练；自然结束停在训练界面。次数、准确率和 Miss 统计只保留在本次会话，退出即丢弃。
接管训练不提交成绩、不写正常成绩库、不生成新回放、不修改源回放。

## 官方 osu!lazer 曲库 / 回放双向同步

设置 → 维护 → **官方 osu!lazer 曲库 / 回放双向同步**。自动检测 `%APPDATA%\osu`（支持 `storage.ini` 重定向），或选择包含 `client.realm` 和 `files` 的数据目录，再手动启用。
**请先关闭官方 lazer**；改版在主菜单 / 选歌中每 15 秒双向同步，数据库占用时等待，不强行写入。
保留原始 lazer 文件，不进行 stable 兼容转换；修改前备份，两端同时变化时提示冲突，整套删除不传播。
改版未关联曲目需选中后“关联 / 发布”。已有本地回放及其成绩记录随曲目双向追加同步，按回放内容去重，不上传、不传播删除。两端必须有完全匹配的谱面版本；仅有在线成绩、未下载回放的不处理。两端保持独立存储，不同步账号设置或皮肤。
写回后重新打开官方版即可看到修改。Windows 实验功能；使用方式、数据库兼容性及中断恢复见 [LAZER_SYNC.md](LAZER_SYNC.md)。

## 界面语言

**跟随 osu! 的语言设置，无需单独切换或重启。** 在游戏设置搜索 Language / 语言，选择简体中文或 English，新增按钮、倒计时、统计和快捷键说明会同步切换。
中英资源已内置；其他语言的新增文本使用英文回退。启动脚本及 .NET 构建日志固定英文，避免控制台乱码。

## 从源码运行

Windows 开发需要 PowerShell 7 (`pwsh`) 和与 `global.json` 相容的 .NET 10 SDK。仓库不附带本地 SDK/依赖缓存；脚本优先使用已有的 `.tools/dotnet`，否则使用系统 `dotnet`。

```powershell
pwsh -NoProfile -File .\ReplayPractice.ps1 Build
pwsh -NoProfile -File .\ReplayPractice.ps1 Test
pwsh -NoProfile -File .\ReplayPractice.ps1 Run
pwsh -NoProfile -File .\ReplayPractice.ps1 Package
```

`Run` 使用独立的 `osu-development-4242` 数据目录。`Package` 生成自包含的 Windows x64 测试包及 SHA256 校验文件，产物放在 `dist/`，不纳入 Git。
完整行为、边界条件和验收范围见 [REPLAY_PRACTICE.md](REPLAY_PRACTICE.md)。

截至 2026-10-04：相关回归 273 通过、1 跳过，PlayerLoader 回归 27 通过；构建及发布版启动检查通过。这不等于整个 osu! 测试套件或所有皮肤/分辨率的人工验收。
本仓库暂未启用 GitHub Actions；上游工作流保留作为源码参考，启用前需检查其官方服务和发布依赖。

## 反馈与许可

接管功能和本测试版的问题请提交到 [本仓库 Issues](https://github.com/post7794/osu-replay-practice/issues)，不要报到官方 osu! 仓库。
请附包名（`build-info.json`）、复现步骤、Mods、窗口尺寸/分辨率和是否使用 Autoplay。日志、谱面及回放按需提供，分享前检查个人信息，不需要提交账号配置或整个数据库。

保留上游的 [MIT 许可](LICENCE) 及源文件版权声明。原始项目说明见 [README.upstream.md](README.upstream.md)；打包产物随附可用的第三方许可信息。

## English quick start

Unofficial osu!lazer replay-takeover practice branch, osu!standard only. Download and fully extract the Windows x64 ZIP from Releases. Run `Start-Replay-Practice.cmd`; the .NET runtime is bundled and test storage is separate from normal osu!.

Watch a replay, seek, then `Ctrl+Enter` to take over after a frozen 3-second countdown. Without a replay, `Ctrl+Enter` in song select starts Autoplay; seek and press it again to take over. AT supplies the historical baseline only and is absent from manual practice. This cannot recover lost real-play input. Other automation mods remain unsupported.

Practice never saves/submits scores or records a replay; normal play is not forced offline. UI text follows osu!'s selected language (English/Simplified Chinese). Report branch-specific issues here, not upstream.
