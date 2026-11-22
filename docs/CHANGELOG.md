# 更新日志

## 1.0.4

- 修复没有 Miss 却出现大量红色菱形、并把准度拉低的问题：重写 Miss 判定，按音符类型分别处理
  - Block：只有 result != 0 才算 Miss
  - 双押（Mul）：走 SetPlayResult，命中记录 played，未命中延迟到该音符长度后确认
  - Ghost / Energy / Music / 普通音符：按 MissCube 判定
  - 长按：只在 SetPlayResult result == 1 时判定，起点记 LongPair
  - 全部用 objId 去重
- 准度公式与 Miss 扣分完全对齐 Info+：Rest 会扣掉真实 Miss 与 LongPair；曲线终点与结算页准度一致

## 1.0.3

- 修复准度曲线与结算页准度不一致：总物量改为按游戏口径统计（可判定音符 + Block/Music/Energy），不再把未判定的音符多算成 Perfect
- 判定采集改用游戏实时计数器 + 逐音符回调（SetPlayResult / MissCube），不再漏 note
- 修复 Miss 红色菱形不显示的问题；音符与幽灵的丢失现在按与 Miss 相同的权重扣准度
- 结算时用游戏自身的准度兜底校准，曲线终点与结算页完全一致
- 重开一局会正确清空上一局数据
- 字体与界面细节优化（统一中文字体、布局、颜色滑杆）

## 1.0.0

- 结算页准度曲线窗口：从 100% 开始显示本局准度变化
- Great 黄色点 / Miss 红色菱形标注
- 窗口可拖动、可缩放、可关闭，位置与大小自动保存
- 线条颜色可调（RGB 滑杆）
- F9 测试预览窗口
