# 界面主题

当前提供“银白 · 浅色”“石墨紫 · 深色”和“黑金女仆 · 深色”。顶部工具栏的太阳/月亮菜单切换主题，默认保留原有银白外观。主题不写入工作流，也不改变节点、连线或画布视口。

## 结构

| 文件 | 职责 |
| --- | --- |
| `src/theme.ts` | 主题注册表、应用和保存偏好；不包含单个主题的样式分支 |
| `src/themes/types.ts` | 统一的主题、可选立绘及节点库角标参数类型 |
| `src/themes/light.ts`、`graphitePurple.ts`、`noirGold.ts` | 每个主题独立的名称、明暗属性、底色、语义配色与素材配置 |
| `src/themes.css` | 所有自定义配色共用的控件样式，不按主题 ID 复制样式 |
| `src/styles.css` | 原有布局和银白外观；银白主题不应用额外配色覆盖 |
| `src/components/CanvasArtwork.vue` | 可选立绘层；不依赖艾酱或任何主题 ID |
| `src/canvasActivity.ts` | 合并滚轮刷新、保持拖动状态、结束/失焦/卸载清理 |
| `public/themes/<id>/` | 各主题的图片和 SVG 素材，Vite 与 Rollup 自动复制 |
| `index.html` | JS 应用挂载前恢复已保存的底色，存储不可用时保留浅色默认值 |
| `../desktop/Core/AppearanceSettings.cs` | 保存主题 ID、明暗属性和启动底色；不维护主题名单 |

`themes` 数组的顺序就是菜单顺序，`defaultTheme` 是明确的回退项；调整菜单顺序不会改变默认主题。菜单的选项、当前主题提示和明暗图标均读取注册表。

## 增加主题

在 `src/themes/` 新建一个定义文件，再导入并加入 `src/theme.ts` 的 `themes` 数组。不需要修改 Vue 控件或桌面端。例如，在 `src/themes/graphiteMint.ts` 中从石墨紫派生一个深色配色：

```ts
import { graphitePurple } from './graphitePurple.ts'
import type { ThemeDefinition } from './types.ts'

export const graphiteMint: ThemeDefinition = {
  ...graphitePurple,
  id: 'graphite-mint',
  name: '石墨薄荷',
  tokens: {
    ...graphitePurple.tokens,
    '--theme-accent': '#8ed9bd',
    '--theme-accent-rgb': '142,217,189',
    '--theme-accent-text': '#cdf1e3',
    '--theme-primary-start': '#386f60',
    '--theme-primary-end': '#2e5b51',
    '--theme-primary-hover-start': '#447f6e',
    '--theme-primary-hover-end': '#386b5e',
    '--theme-toggle-start': '#568c7b',
    '--theme-toggle-end': '#74b7a0'
  }
}
```

然后在注册表导入 `graphiteMint`，将它追加到现有数组。以上仅为扩展示例，当前没有注册薄荷主题。

- `id` 必须唯一、稳定，以小写字母开头，只含小写字母、数字和连字符，最长 64 字符；更改显示名称不必更改 ID。
- `colorScheme` 为 `light` 或 `dark`，影响浏览器原生输入控件和菜单图标。
- `background` 使用 `#RRGGBB`，同步网页、启动窗、主窗口和 WebView2 底色。
- 新主题应提供完整的 `tokens`，推荐继承已有配色后覆盖。只有原始银白主题省略 `tokens`，以保留现有控件外观。
- 调整强调色时，同时检查文字、按钮、悬停和开关；浅色新主题还需更换表面、文字、边框与阴影等整套变量。
- `--theme-on-primary` 是主要按钮上的文字；金色等亮按钮可用深色文字。此时单独提供 `--theme-on-danger`，避免深色危险按钮的文字变暗；`--theme-on-check` 控制复选框勾号，默认白色。
- 新增控件颜色时，在共享样式中引用语义变量；避免加入 `[data-theme="某个主题"]` 分支。节点种类和端口使用原有语义色，不随强调色丢失区分。
- 端口中心与最外圈统一使用 `--port-color`，普通、悬停、连接吸附及连接提示动画均保持同色；新主题也沿用该规则，不用页面背景色覆盖外圈。中间描边可跟随主题调整。

## 增加二次元主题

以黑金女仆为起点，在独立定义文件内换角色、锚点和节点库四角素材即可；配色仍可在 `tokens` 中逐项覆盖。例如：

```ts
import { noirGold } from './noirGold.ts'
import type { ThemeDefinition } from './types.ts'

export const anotherMaid: ThemeDefinition = {
  ...noirGold,
  id: 'another-maid',
  name: '另一位女仆',
  artwork: {
    ...noirGold.artwork,
    src: './themes/another-maid/portrait.png',
    anchor: 'bottom-right',
    opacity: .4,
    interactionOpacity: .15
  },
  libraryOrnament: {
    cornerMask: './themes/another-maid/corner.svg',
    opacity: .5
  }
}
```

把素材放入 `public/themes/another-maid/`，然后在 `src/theme.ts` 导入并注册。无需向 `App.vue`、`WorkflowCanvas.vue` 或 CSS 加入主题名称判断，也不必手工修改资源复制清单。此示例保留黑金配色，只演示更换角色和装饰。

`artwork`、`libraryOrnament` 都可省略。继承带立绘的主题后想去掉装饰，显式设置 `artwork: undefined`、`libraryOrnament: undefined`。切回普通主题时，立绘组件卸载、节点库装饰层隐藏且对应变量移除，交互计时器清空。

| 立绘字段 | 默认值 | 作用 |
| --- | --- | --- |
| `src` | 必填 | 相对页面的本地素材路径，建议透明 PNG |
| `anchor` | `bottom-left` | 也支持 `bottom-right`；不镜像人物 |
| `heightRatio` | `0.55` | 相对于可见画布高度，跟工作流 zoom 无关 |
| `minHeight` / `maxHeight` | `180` / `380` | 显示高度上下限，单位 CSS px；还受容器边界约束 |
| `opacity` | `0.45` | 静止时透明度，范围 0–1 |
| `interactionOpacity` | `0.2` | 拖动、缩放和连线时透明度，范围 0–1 |

节点库角标用 `libraryOrnament.cornerMask` 指定左上角形状的透明 SVG，其余三角由共享样式镜像生成，通过主题强调色着色；`libraryOrnament.opacity` 默认 `0.55`，允许 `0`。此配置只作用于节点库四角，不提供标题栏、状态栏或程序外框四角的装饰扩展。原通用 `ornament` 字段已移除，新增或迁移主题请使用 `libraryOrnament`。

节点库的 `.sidebar` 是不滚动的固定外框，四角装饰统一放在外框内的 `.sidebar-ornaments` 独立层；只有旁边的 `.sidebar-scroll` 内容区滚动。不要把角标挂到滚动区，否则会跟随列表移动。装饰层不参与内容动画且不拦截输入；抽屉开合和裁剪统一作用于外框及其装饰。窄窗口抽屉距离顶部功能区、左侧窗口边缘和底部状态栏均为 `--sidebar-drawer-gap`（默认 `14px`），由共享布局计算，不在主题中分别配置。

### 共用交互规则

- 立绘是画布视口中的独立层，位于连线和节点下方；不是节点，不进入工作流变换，也不参加布局。窗口容器变大或变小时可以响应式调整大小，拖动画布或缩放节点时不移动、不放大人物。
- 立绘及图片均 `pointer-events: none`，不拦截点击、滚轮、拖动、连线；对辅助技术隐藏，图片不可拖出。素材加载失败时隐藏装饰，不阻塞主界面。
- 开始平移、实际节点拖动、节点大小调整或连线时淡化。滚轮和缩放按钮共用合并计时器，停止输入 150ms 后用 200ms 恢复；按住指针的过程中不会因暂停移动而反复恢复。
- 淡化只改变 opacity，不在每个 pointermove 中更新 Vue 状态或触发额外布局；失焦、换主题、卸载时清理状态和计时器。
- 默认不启用视差、呼吸或循环动画；尊重系统“减少动态效果”，关闭透明度过渡。

### 素材与检查清单

- 必须使用真实 alpha 透明素材，不能把棋盘格预览当作透明背景。黑金主题采用用户确认的最新重绘立绘，人物 RGB 保持不变，仅处理背景 alpha。
- 素材应留出发饰和头发边缘，底部由共享渐隐遮罩融入画布。按 180–380px 显示时检查细发丝边缘，不把背景、文字或按钮画进立绘。
- 检查所有控件的普通、悬停、选中、禁用状态，尤其是主按钮/危险按钮文字和复选框勾号的对比度。
- 检查 1280×720、760×560，确认立绘不遮挡操作；测试平移、滚轮、节点拖动/缩放、连线和失焦。
- 在普通侧栏和窄窗口抽屉中滚动节点库到中间、底部再返回，确认四枚角标始终贴在节点库面板四角，且不拦截点击或滚轮；程序外框四角不显示装饰。
- 窄窗口打开抽屉后，检查上方功能区、左侧窗口边缘、下方状态栏与抽屉的间距一致（默认 `14px`）；关闭和重新打开、跨布局缩放后仍然正确。
- 往返切换银白、石墨紫和新增主题，检查没有残留素材、颜色、计时器；工作流节点和视口不得因换主题而改变。
- 运行下方测试和生产构建；确认 `desktop/wwwroot/themes/<id>/` 及本地发布目录内包含对应素材。正式构建只打包最终素材，不依赖生成工具的用户目录。

## 偏好与启动

浏览器键 `aichan:theme` 保存 `{ id, colorScheme, background }`，供页面最早阶段恢复底色。桌面偏好位于 `%LOCALAPPDATA%/AichanToolbox/appearance.json`，原子写入；启动窗在显示之前读取它。桌面偏好优先，页面挂载前会解析注册表并同步最新颜色。

未知或已移除的主题 ID 在前端回退到银白；损坏、不完整或不可读的桌面配置也回退到银白。存储失败不会阻止当前界面切换，界面会提示不能保存。清除 WebView2 缓存不应清除独立的桌面偏好。

开发环境通过 CSS 导入加载样式；Rollup 生产构建按顺序合并 `styles.css` 和 `themes.css`，两者复用同一 HTML 启动模板。

## 验证

在 `frontend` 目录运行 `pnpm test` 和 `pnpm build`；在仓库根目录运行：

```powershell
dotnet run --project tests/AichanToolbox.StartupSmoke/AichanToolbox.StartupSmoke.csproj -c Release
```

现有测试覆盖启动底色恢复、损坏/禁用存储回退、浅色变量清理、新主题注册、可选装饰清理、交互合并/按住/重入/失焦/卸载，以及桌面偏好持久化和原生背景同步。新增主题还应按上方清单检查实际画面。
