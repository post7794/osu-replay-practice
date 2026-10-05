osu! Replay Practice - 非官方回放接管测试版
=========================================

仅用于测试，不是 osu! 官方发行版。
Windows 10/11，64 位 x64。已包含 .NET 运行时，不需要安装 SDK。

启动与数据
----------
1. 完整解压 ZIP 到一个可写的文件夹，不要直接在压缩包内运行。
2. 双击 Start-Replay-Practice.cmd 或 osu!.exe。
3. 自行导入需要测试的谱面 (.osz) 和匹配回放 (.osr)。
   可以把文件拖入游戏窗口；不要为此更改系统默认打开方式。
4. 开始观看 osu!standard 回放后，即可从右侧面板接管训练。
   没有真实回放时，在选歌界面按 Ctrl+Enter 启动 Autoplay，
   定位到想练的部分，再按 Ctrl+Enter 接管。

本包没有包含开发者下载的谱面、回放、皮肤、账号或配置。
程序仍包含官方资源依赖中的内置界面、音效等资源。
默认测试数据目录：%APPDATA%\osu-replay-practice-test
这是独立的测试目录，不使用正常 osu! 或开发客户端的数据目录。
请勿把游戏内存储位置改成正常客户端的数据目录。
直接双击 exe 也保持隔离；不抢占正式客户端的 IPC 或旧版 TCP 通道。
本包不安装文件关联，禁用自动更新；更新时重新解压新的测试包。
删除程序文件夹不会删除上述测试数据目录。

训练使用
--------
Ctrl+Enter             立即接管当前帧
Space / 鼠标中键       暂停或继续
A / D                  上一个 / 下一个物件
Q / E                  后退 / 前进 1 秒
, / .                  后退 / 前进 1 个回放帧
N / Shift+N            下一个 / 上一个原回放 Miss
M / Shift+M            下一个 / 上一个原回放忽略（少加 combo 但不断连）
W / S                  加速 / 减速
F                      重置额外速度
Ctrl+Shift+Enter       重试当前练习起点
Ctrl+Backspace         返回原回放，在练习起点暂停
Ctrl+Shift+Backspace   直接退出回放或训练

可在右侧“快捷键”或官方设置里的 Replay 按键分组重新配置。
如果 A/D、Q/E、W/S 等与自己的打击键冲突，先修改这些绑定。
游戏设置选择“简体中文”可切换新增界面的语言。
接管和重试有 3 秒倒计时；恢复历史时可能短暂等待，不显示历史快放。
选择物件后接管，默认忽略之前的物件，便于练习重叠滑条。
失败不中断；曲目结束后留在训练中，可重试或退出。

当前限制
--------
支持 osu!standard 的真实回放接管和 Autoplay（AT）自动基线接管。
CN/RX/AP/SO 等其他自动操作 Mods 不支持接管；普通观看仍可使用。
回放必须与导入的谱面匹配。
上一 / 下一物件会显示金色定位框和“当前 #编号”（物件顺序，不是连击数）。
重叠时按住侧栏“聚焦当前物件”可淡化其他物件；松开或移开鼠标恢复。
标记独立于皮肤；恢复播放或正式开打时自动隐藏，不改变时间或判定。
内置资源不等于已经导入的测试谱面；需要测试者自行准备素材。
Autoplay 基线只在内存中生成一次；接管后移除 AT，其他 Mods 保留。
重试及定位复用相同基线；返回观看会恢复 AT，并在练习起点暂停。
这是自动操作的现场，不是找回中途退出那局的真实回放。
原来的失误和血量等实际历史无法恢复；面板会明确标注“自动生成基线”。

重要：只有“接管训练”不提交成绩、不写正常成绩库、不生成新回放。
整个客户端不是强制离线模式；普通游戏、登录和导入仍按原流程工作。
测试接管功能不需要登录账号。

建议测试
--------
- 播放中接管、滑条/转盘中途接管、倒计时预按键。
- 连续重试是否保持相同起点、速度，是否出现状态残留。
- A/D、进度条、上一个/下一个 Miss 与忽略。
- 暂停、重试、直接退出、返回回放。
- 中英文切换、修改快捷键、不同窗口大小、拖动和缩放菜单。

反馈时请提供 build-info.json 里的包名、复现步骤、Mods、分辨率，
以及实际回放/谱面是否为同一版本。
如发生崩溃，可附测试数据目录下 logs 中对应时间的日志。
分享日志前自行检查账号名、聊天、文件路径等信息。
提交谱面或回放前确认自己愿意分享；不需要发送账号配置或整个数据库。

English quick start
-------------------
Unofficial Windows x64 test build. Extract the entire ZIP, then run
Start-Replay-Practice.cmd or osu!.exe. No .NET SDK/runtime installation required.
Import your own matching .osz/.osr files; none of the developer's data is bundled.
The default data folder is %APPDATA%\osu-replay-practice-test.
Updates and file associations are disabled. Do not redirect storage to your
normal osu! installation. Ctrl+Enter takes over; Space pauses; Ctrl+Shift+Enter
retries; Ctrl+Backspace returns; Ctrl+Shift+Backspace exits directly.
Only takeover practice disables score saving/submission and replay recording.
Normal play is not forced offline. Logging in is unnecessary for this test.
Without a replay, Ctrl+Enter in song select starts Autoplay. Seek to your target,
then Ctrl+Enter takes over. AT is removed in manual practice; other mods remain.
The generated baseline is kept only in memory and reused on retry/seek/return.
It does not recover your lost play history. CN/RX/AP/SO and non-standard rulesets
still cannot be taken over.
Include the package name from build-info.json when reporting issues.

Official osu!lazer two-way sync / 官方 lazer 曲库双向同步
Settings > Maintenance > Official osu!lazer two-way beatmap / replay sync.
Close official lazer first. Detect or select its data directory containing client.realm and files.
Enable sync explicitly. Never share the two clients' entire data directories.
Raw beatmap and resource changes sync both ways while this client is in menus.
Saved local replays and their score metadata also sync both ways (content deduplication, no uploads or deletion propagation). Matching beatmap versions are required; online-only scores are skipped. Reopen official lazer after syncing. Skins and account settings are not synced.
See LAZER_SYNC.md for backups, conflicts, schema compatibility and interrupted-write recovery.
设置 → 维护 → 官方 osu!lazer 曲库 / 回放双向同步。先关闭官方版，再检测数据目录并启用。
不是 Songs 同步，不要选择程序安装目录或 files 子目录。建议先测试曲库副本。
已保存的本地回放会连同成绩记录双向同步，按内容去重，不上传或传播删除。
两端必须有完全相同的谱面版本；在线成绩未下载回放的不处理。同步后在选歌的本地成绩中观看。