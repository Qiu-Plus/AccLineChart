# 第三方组件声明

本仓库代码以 MIT 协议发布（见 `LICENSE`）。运行时还依赖以下第三方组件，它们不包含在本仓库源码中：

| 组件 | 用途 | 许可 | 来源 |
|---|---|---|---|
| [Iced](https://github.com/icedland/iced)（随 MelonLoader 提供） | x64 指令解码 / 重定位，用于自写的最小 detour（只读读取 Euterpe 联机成绩帧） | MIT | 随 MelonLoader 安装，不随模组分发 |
| [MelonLoader](https://github.com/LavaGang/MelonLoader) 0.7.3 | Mod 加载器 / Harmony | Apache-2.0 | 游戏侧环境 |
| [SkiaSharp](https://github.com/mono/SkiaSharp)（可选） | 解码 Euterpe 玩家头像（WebP）；缺失时使用颜色圆点代替 | MIT | 若 `UserLibs` 中已有则自动使用 |

本项目不包含、不修改 Euterpe 或 Muse Dash 的任何文件；只在游戏进程内以只读方式复制 Euterpe 自己收到的 WebSocket 明文帧。
