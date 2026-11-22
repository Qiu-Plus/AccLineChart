# AccLineChart

Muse Dash 结算页准度曲线模组（MelonLoader）。打完一首歌后，在结算页弹出一个可拖动、可缩放的准度变化曲线窗口。

![结算页效果](docs/screenshots/01-results-chart.png)

## 功能

- 结算页显示本局准度随时间变化的曲线；
- 曲线从 **100%** 出发：以整张谱面的音符数为分母，未判定的音符按 Perfect 预扣，只有 **Great（黄色点）** 和 **Miss（红色菱形）** 会让曲线下降；
- 窗口可拖动（标题栏）、可缩放（右下角）、可关闭（右上角 X）；
- 线条颜色可用 RGB 滑杆调整；
- 窗口位置、大小、颜色保存在 `UserData/AccLineChart.cfg`；
- 快捷键 **F9**：打开/关闭测试预览窗口（假数据）。

## 环境要求

- Muse Dash（Steam 版，开发时基于 6.8.0）
- [MelonLoader](https://github.com/LavaGang/MelonLoader)（开发时基于 0.7.3）
- 构建需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)（项目目标框架仍是 `net6.0`）

## 构建

项目使用 [MuseDash.Mod.Template](https://www.nuget.org/packages/MuseDash.Mod.Template) 的 MSBuild SDK：`MuseDash.Mod.Sdk` 会自动在 Steam 库中查找游戏，引用 MelonLoader 与游戏程序集，并在每次构建后把 DLL 复制到游戏的 `Mods` 目录。

```powershell
cd src
dotnet build -c Release
```

如果游戏不在 Steam 默认库中，设置环境变量 `MD_DIRECTORY` 指向游戏目录，或在项目旁创建 `AccLineChart.csproj.user`：

```xml
<Project>
  <PropertyGroup>
    <GameFolder>D:\Games\Muse Dash</GameFolder>
  </PropertyGroup>
</Project>
```

构建产物：`artifacts/bin/AccLineChart/release/AccLineChart.dll`

可选：设置 `MD_DEPLOY_DIR` 可把构建结果额外复制到指定目录：

```powershell
$env:MD_DEPLOY_DIR = "D:\some\staging\"
dotnet build -c Release
```

## 安装

把 `AccLineChart.dll` 放进游戏目录的 `Mods` 文件夹，重启游戏。

## 配置

配置文件：`UserData/AccLineChart.cfg`（MelonPreferences）

| 项 | 说明 |
|---|---|
| `LineColorR/G/B` | 线条颜色（0~1） |
| `WindowX/Y/W/H` | 窗口位置和大小 |
| `ShowPerfectDots` | 是否用小白点标注 Perfect（默认关） |
| `DebugLog` | 调试日志（默认关） |

## 说明

- 不联网、不读写游戏存档、不修改游戏或 Euterpe 文件，只写自己的配置文件；
- 当前版本只统计自己这一局的准度，不包含联机多人画面；
- 曲线在打完时与游戏结算的准度一致。

## 作者

巯_QiuPlus
