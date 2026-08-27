# 更新日志

本文件记录艾酱图片工具箱面向用户的重要变更。

## [8.0.1] - 2026-08-27

### Slim ZIP 封装修订

- 正式 Slim ZIP 改用已验证的 RepackTest1 封装，仍使用原文件名及 8.0.1 版本号；不重新编译、不移动版本标签，Portable 包保持不变。
- 包内 33 个文件的路径、长度和逐文件 SHA-256 与原正式包完全一致；只改变压缩数据、条目顺序及 ZIP 元数据，未包含诊断性依赖升级或 Diagnostic1 构建。
- ZIP 从 13,693,761 字节降至 13,006,975 字节；31 个文件使用 Deflate，2 个 PNG 使用 Store。
- Slim ZIP 新 SHA-256 为 `5a016f569ea5cc45f049175e251cf8478beeabcdfdbb8f79875b3161571da700`；此前同名 ZIP 的 SHA-256 为 `5474fb51389297258eae5f1a7bdcd27a39c62336531c71cc74ec0216643dbca3`，发行附件中的 `SHA256SUMS.txt` 同步更新。
- 本机对照中，RepackTest1 经真实浏览器下载并保留互联网来源标记后，指定 Microsoft Defender 扫描未发现威胁，未复现原 ZIP 的下载拦截；正式 Slim 附件替换后也已重新下载、核对校验值并复扫，未发现威胁。上述结果不是 Microsoft 的误报认定或跨设备安全保证。

### 启动与性能

- 启动窗改为先于主窗口和 WebView2 初始化显示，降低冷启动阶段的黑屏感。
- 移除启动窗实时阴影，改用静态径向光晕；Logo 呼吸动画限制为 30 FPS，退出淡出由 170ms 缩短至 110ms。
- 生产前端启用 Terser 两轮压缩并关闭未使用的 Vue Options API，`app.js` 从约 460 KB 降至约 161 KB。
- 扩展启动遥测，分别记录 HTML、脚本与样式资源、DOM、首帧、Vue 挂载、启动快照和 WebView2 阶段。
- 对 ReadyToRun 进行了交替冷启动测量；因发布目录增大约 14.7% 且启动中位数没有改善，本版本维持普通发布方式。

### 界面与构建

- 画布与节点标题栏改用艾酱主题 `28×28px` 透明拖拽光标，移除模糊星星装饰，并重新处理透明边缘与箭头热点。
- 前端构建会先安全清理桌面端 `wwwroot` 输出目录，避免旧光标和过期静态资源残留。
- 发布包继续提供 Portable 与 Slim 两种 Windows x64 版本；默认不包含可选 FFmpeg 兼容组件。

## [8.0.0] - 2026-08-26

- 桌面端迁移到 .NET 10，便携版内置 .NET Desktop Runtime 10.0.11。
- 目标体积压缩增加“达标”和“未达标”双出口，以及未达标回退或保留最小候选机制。
- 节点与 WebGL 连接线统一坐标和合成路径，改善缩放、拖动稳定性与连接线曲率。
- 完善 JPG 输出语义、ZIP 工作流安全、替换文件回收站处理和引擎冒烟测试。

[8.0.1]: https://github.com/RukaPrPr/AichanToolbox/compare/v8.0.0...v8.0.1
[8.0.0]: https://github.com/RukaPrPr/AichanToolbox/releases/tag/v8.0.0
