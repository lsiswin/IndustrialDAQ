# ColdVision 瓶盖有无检测 — 模拟样本库

## 文件清单（10 张，1024×1024 PNG）

### OK 样本（4 张）— 瓶盖存在且正确
- 01_OK-perfect-cap.png         完美瓶盖 · 标准光照
- 02_OK-cap-with-reflection.png 瓶盖存在 · LED 反光场景
- 03_OK-dim-light.png           瓶盖存在 · 偏暗光照
- 04_OK-multi-bottles.png       多瓶视野 · 全部正确

### NG 样本（6 张）— 各种质量缺陷
- 05_NG-missing-cap.png          缺盖 · 瓶口暴露
- 06_NG-missing-cap-blurred.png  缺盖 · 运动模糊
- 07_NG-tilted-cap.png           瓶盖倾斜错位
- 08_NG-broken-cap.png           瓶盖碎裂
- 09_NG-wrong-color.png          错色瓶盖（黄色代替红色）
- 10_NG-dented-cap.png           瓶盖凹陷变形

## 用途说明

本样本库用于 ColdVision 瓶盖有无检测 MVP 阶段的：
1. 模拟相机取图源（替代真实工业相机）
2. ROI 选择与 OpenCV 模板匹配训练数据
3. 报警规则触发演示
4. NG 图片自动保存机制验证
5. UI 实时展示效果演示

## 集成建议

将本目录复制到 D:\CapImages\ 作为 ColdVision 模拟相机取图源。
在系统配置中将帧间隔设为 33 ms（约 30 fps）即可循环播放。

## 视觉一致性

所有图片均采用：
- 顶视（俯拍）固定机位
- 深灰工业传送带背景
- 红色标准瓶盖作为正样本基准
- 1024×1024 分辨率（便于 OpenCV 处理）
- 高清晰度（便于模板匹配训练）

— ColdVision 工业机器视觉平台
