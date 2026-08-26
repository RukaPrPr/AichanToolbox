<p align="center">
  <img src="frontend/assets/aichan-program.png" width="112" alt="艾酱图片工具箱 Logo">
</p>

<h1 align="center">艾酱图片工具箱</h1>

<p align="center">
  面向 Windows 的节点式批量图片处理工具。自由组合筛选、缩放、JPG 压缩、目标体积分流与 ZIP 自动化流程。
</p>

<p align="center">
  <a href="https://github.com/RukaPrPr/AichanToolbox/releases/latest"><strong>下载最新版</strong></a>
  · <a href="#快速开始">快速开始</a>
  · <a href="#节点一览">节点一览</a>
  · <a href="#从源码构建">从源码构建</a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-8.0.0-7774d8?style=flat-square" alt="Version 8.0.0">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet" alt=".NET 10">
  <img src="https://img.shields.io/badge/platform-Windows%20x64-0078D4?style=flat-square&logo=windows11" alt="Windows x64">
  <img src="https://img.shields.io/badge/Vue-3.5-42B883?style=flat-square&logo=vuedotjs" alt="Vue 3.5">
</p>

## 简介

艾酱图片工具箱把批量图片处理组织成一张可视化工作流：图片从导入节点进入，经格式、体积或分辨率条件分流，再按需要执行转换、缩放、逆网点化和 JPG 压缩，最后保存文件或重新打包 ZIP。

桌面端由 C#、WPF 与标准 WebView2 承载，界面使用 Vue 3 和 TypeScript；图片处理以 libvips 为核心，JPEG 由 Jpegli 进行 4:4:4 高画质编码。工作流会延迟生成中间文件，尽量从同一工作底图直接得到最终结果，避免不必要的重复解码和代际压缩。

## 8.0.0 更新重点

- 全面迁移到 `.NET 10`：开发基线为 `net10.0-windows`，SDK 固定为 `10.0.400`，便携版内置 .NET Desktop Runtime `10.0.11`。
- “目标体积压缩”增加“达标”和“未达标”双出口；最多 5 次真实编码后，可以选择输出最小候选，或完整回退到节点入口状态后继续补救流程。
- 节点从挂载、选中、拖动到松开始终使用同一条 `translate3d` 渲染路径，解决低缩放比例下文字闪烁和粗细变化。
- 节点标题栏与画布统一使用高清矢量张手/握手光标；拖动经过其他控件时不会切回系统光标。
- 连接线控制点改为连续函数，端点跨越垂直位置时不再突然改变曲线形状。
- 补充目标体积分流、旧工作流兼容、JPG 路径校验及二次缩放重试的引擎冒烟测试。

完整安装包与更新说明见 [GitHub Releases](https://github.com/RukaPrPr/AichanToolbox/releases)。

## 下载与运行

从 [Releases](https://github.com/RukaPrPr/AichanToolbox/releases) 下载适合自己的 `win-x64` 版本：

| 版本 | 适合场景 | 运行条件 |
| --- | --- | --- |
| Portable 便携版 | 下载后直接使用，适合分发给未安装 .NET 的电脑 | 已包含 .NET 10 Desktop Runtime；仍需要 WebView2 Runtime |
| Slim 精简版 | 文件更小，适合已经配置好运行环境的电脑 | 需要 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) 与 WebView2 Runtime |

系统要求：

- Windows 10/11 x64。
- Microsoft Edge WebView2 Runtime。Windows 10/11 通常已经安装；精简系统可从 [Microsoft WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) 获取。
- 将压缩包完整解压到有写入权限的目录。程序由多个文件组成，请勿只移动或直接从 ZIP 内运行 `AichanToolbox.exe`。

默认发行包不包含体积较大的 FFmpeg。PNG、JPG/JPEG、WebP、AVIF、HEIC 等常用格式优先由 libvips 解码；只有 libvips 无法读取的少数格式才需要额外的 `ffmpeg.exe` 兼容组件。

## 快速开始

1. 双击 `AichanToolbox.exe`。
2. 在“导入 / 文件列表”节点中选择图片，或先用“ZIP 解压”导入一个或多个压缩包。
3. 从节点右侧出口拖到下一节点左侧入口，组合自己的处理和分支逻辑。
4. 需要确认实际输出体积时，先运行“精确预估”；开启“缓存预估”后，正式运行可以复用已生成结果。
5. 检查保存位置与“替换原文件”等危险选项，然后点击“运行工作流”。

画布支持空白区域拖动、围绕指针滚轮缩放、“适应”显示全部节点、节点复制与多选置顶。顶部可以选择 1—16 路并行、启用黑白优化、保存或切换工作流配置。

## 节点一览

| 分类 | 节点 | 主要作用 |
| --- | --- | --- |
| 预处理 | ZIP 解压 | 批量解压 ZIP；支持自动、UTF-8、GB18030、CP932 文件名编码和内存密码 |
| 输入与分支 | 导入 / 文件列表 | 导入并勾选图片，显示格式、体积和分辨率 |
| 输入与分支 | 格式筛选 | 按 JPG、PNG、WebP、其他四个出口分流 |
| 输入与分支 | 大小筛选 | 按所在节点入口处的真实当前文件体积进行符合/不符合分流 |
| 输入与分支 | 分辨率筛选 | 按宽、高以及 AND/OR 组合条件分流 |
| 图片处理 | 转为 JPG | 使用 Jpegli 输出 4:4:4 JPEG；透明区域合成到白色背景 |
| 图片处理 | 按比例缩放 | 20%—100% 等比缩放，使用 Lanczos 重采样 |
| 图片处理 | 逆网点化 | 轻微、中度、强力三档灰度去网平滑 |
| 图片处理 | JPG 画质压缩 | 20%—100% Jpegli 输出画质，默认 100% |
| 图片处理 | 目标体积压缩 | 最多 5 次真实编码，分别从“达标”或“未达标”出口继续 |
| 输出 | 保存输出 | 保存到原图目录或指定目录，也可在确认后替换原文件 |
| 后处理 | ZIP 压缩 | 按来源 ZIP 分别以 Store 模式重新打包，可保留非图片文件和目录结构 |
| 后处理 | 删除解压目录 | 仅在新 ZIP 完整写入并校验成功后删除本次登记的解压目录 |

图片端口和批次端口类型不同，无法错误连接。一个出口只保留一条连接，重新连接会替换该出口原有去向。

## 目标体积压缩

目标体积节点使用同一份无损工作底图进行真实 Jpegli 编码，并结合单图对数预测和同节点批次历史提示，在最多 5 次尝试内寻找“不超过目标体积的最高已验证画质”。预测只用于选择下一次画质，任何最终候选都必须真实编码并测量。

| 结果 | 出口 | 输出内容 |
| --- | --- | --- |
| 已找到不超过目标体积的候选 | 达标 | 最高已验证画质的 JPG |
| 5 次内未达标，开关关闭（默认） | 未达标 | 丢弃全部试算文件，原样恢复进入节点时的格式、尺寸和处理状态，相当于跳过本节点 |
| 5 次内未达标，开关开启 | 未达标 | 5 次真实编码中体积最小的 JPG，便于接入缩放节点后二次尝试 |

编码器故障、文件读取错误和用户取消属于运行错误，不会被伪装成“未达标”。旧工作流继续使用原来的 `out` 端口 ID；若目标失败但没有连接“未达标”出口，程序会给出明确提示。

## 图片处理语义

- 格式、体积和分辨率筛选读取该节点入口处的真实状态，不会提前把导入文件伪装成 JPG。
- 如果一条分支只经过筛选、没有执行任何图片修改，可以直接到“保存输出”；结果会标记为“不处理”并保留原文件。
- 只要执行了缩放、逆网点化等实际修改，抵达保存节点前就必须经过能够生成 JPG 的节点。运行前静态检查会拒绝无效路径，不会在保存阶段偷偷补转格式。
- 单次 JPG 输出按 32 行像素条带从 libvips 流向 Jpegli，不生成并回读中间 PNG；目标体积节点为了多画质复用，会有意保留一份压缩的无损工作底图。
- “黑白优化”默认开启，只对 RGB 通道差异极小的明确黑白内容采用单通道编码；泛黄扫描、低饱和彩页和明确彩色内容保持原色。
- “精确预估”会真实执行工作流；开启缓存后，正式运行可直接复用签名一致的中间结果。

## ZIP 工作流与文件安全

典型 ZIP 流程：

```text
ZIP 解压 → 导入 / 文件列表 → 图片处理与分支 → 保存输出 → ZIP 压缩 → 删除解压目录
```

- 每个源 ZIP 解压到同目录的独立同名文件夹；若目录已存在，会创建带编号的新目录，不覆盖旧内容。
- 解压密码只保留在当前运行内存中，不写入工作流配置。
- 解压会拒绝越界路径，并限制异常条目数量和异常膨胀体积。
- ZIP 压缩固定使用 Store/仅存储模式，避免再次压缩已经压缩过的图片数据。
- 启用“替换原 ZIP”时，只有新 ZIP 完整写入并通过读取校验后才会替换源包。
- “删除解压目录”只识别当前解压节点创建并登记的目录；图片处理或打包失败时不会删除。该操作是永久删除，不进入回收站，运行前会再次确认。
- “替换原文件”会等待源文件释放，并通过专用 STA 队列交给 Windows Shell 移入回收站；再次运行时可以选择将上次输出采纳为新的输入基线。

## 界面与性能

- DOM 节点始终使用同一套 `translate3d` 世界坐标；指针移动按 `requestAnimationFrame` 合并，每个显示帧最多提交一次交互更新。
- 连接线由固定视口 WebGL 图层绘制，DOM 节点和连接线共享坐标矩阵；缩放、平移和拖动时保留完整彩色光晕。
- 端口坐标在拖动开始时缓存，拖动期间不逐帧读取所有节点布局。
- 窗口宽度小于 1180px 时，左侧节点库会变成顶部毛玻璃抽屉；跨断点使用 FLIP 动画并保持画布中心。
- 启动时先一次性加载版本、配置、文件与工作流快照；图片引擎和历史缓存清理由后台延迟初始化，减少启动阻塞。
- 自定义矢量光标、WebGL 曲线和节点合成路径均适配 Windows 高 DPI 与画布 30%—200% 缩放。

## 技术架构

| 层级 | 实现 |
| --- | --- |
| 桌面宿主 | C# / WPF / .NET 10 / Microsoft Edge WebView2 |
| 前端界面 | Vue 3 / TypeScript / Pinia |
| 工作流渲染 | DOM 节点 + 固定视口 WebGL 连接线 |
| 图片核心 | NetVips / libvips，Lanczos3 缩放与色彩处理 |
| JPEG 编码 | Jpegli `cjpegli.exe`，4:4:4 或明确黑白图的单通道输出 |
| ZIP 处理 | SharpCompress，路径越界和异常膨胀防护 |
| 兼容解码 | 可选 FFmpeg，仅在 libvips 无法读取时回退 |

工作流配置保存在 `%LOCALAPPDATA%\AichanToolbox\workflow-profiles.json`。处理缓存位于程序目录的 `Cache` 文件夹；正常退出会删除当前会话缓存，下次启动也会清理异常退出遗留的旧会话目录。

## 从源码构建

开发环境：

- Windows x64 与 PowerShell。
- .NET 10 SDK `10.0.400`；仓库根目录的 `global.json` 会固定 SDK 基线。
- Node.js 与 pnpm。

```powershell
git clone https://github.com/RukaPrPr/AichanToolbox.git
cd AichanToolbox

# 前端类型检查、Rollup 打包与 WPF Release 构建
.\build.ps1

# 框架依赖发布
.\build.ps1 -Publish

# 自包含发布
.\build.ps1 -Publish -SelfContained

# 同时生成 Portable 与 Slim 两个目录
.\publish-distributions.ps1
```

首次构建会在需要时安装前端依赖。默认不打包 FFmpeg；如确需兼容组件，将 `ffmpeg.exe` 放在 `vendor/ffmpeg/`，再传入 `-IncludeFfmpeg`。

## 测试

```powershell
# 完整构建
.\build.ps1

# 图片、工作流、目标体积分流、替换与 ZIP 引擎冒烟
dotnet run --project .\tests\AichanToolbox.EngineSmoke\AichanToolbox.EngineSmoke.csproj -c Release -- "$(Get-Location)"

# 目标体积探针构建
dotnet build .\tests\AichanToolbox.TargetSizeProbe\AichanToolbox.TargetSizeProbe.csproj -c Release
```

正式发布前还应分别启动 Portable 与 Slim 包，确认 WPF、WebView2、前端资源和对应 .NET 运行方式均可正常加载。

## 仓库结构

```text
desktop/                    WPF 宿主、图片引擎、工作流执行器与 ZIP 服务
frontend/                   Vue 3 / TypeScript 界面与 WebGL 连接线渲染器
tests/AichanToolbox.EngineSmoke/
                            图片、工作流、替换与 ZIP 冒烟测试
tests/AichanToolbox.TargetSizeProbe/
                            目标体积编码探针
vendor/jxl-v0.11.2-win-x64/ Jpegli 编码器及许可证
build.ps1                   构建和单版本发布入口
publish-distributions.ps1   Portable / Slim 双发行入口
```

第三方组件及许可证信息见 [`desktop/THIRD-PARTY-NOTICES.txt`](desktop/THIRD-PARTY-NOTICES.txt)。发行包会同时携带所需组件的许可文件。
