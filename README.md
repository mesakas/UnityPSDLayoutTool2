# UnityPSDLayoutTool2

`UnityPSDLayoutTool2` is a maintained fork of **UnityPSDLayoutTool**.

This project is explicitly based on the original `UnityPSDLayoutTool` and adds compatibility fixes and workflow improvements for newer Unity versions.

## 中文说明

`UnityPSDLayoutTool2` 是基于原始 **UnityPSDLayoutTool** 的维护增强版，重点补充了新版 Unity 兼容和中文工作流支持。

### Unity 版本支持

- 已验证可用：**Unity 6000.3.7f1**
- 原版插件面向较早期 Unity 版本；本项目在其基础上增加了对新版本 Unity 的适配。

### 相比 UnityPSDLayoutTool 的修改

1. 适配新版 Unity 编辑器 API。
2. 修复 PSD Unicode 解码，支持中文图层名与中文文本正确导入。
3. 增加中文友好的字体回退策略，降低中文文本乱码/方块字问题。
4. 增加更稳定的渲染顺序，减少相机角度变化导致的图层遮挡异常。
5. 增加导出目录可配置能力（资源输出目录可调整）。
6. 增加 Prefab 输出位置可配置：
   - 默认：Prefab 输出到生成资源文件夹的同级目录
   - 可选：Prefab 输出到生成资源文件夹内部
7. 本分支重命名：
   - 插件目录：`Assets/PSDLayoutTool2`
   - 命名空间：`PsdLayoutTool2`

### 安装

把以下目录复制到 Unity 项目中：

- `Assets/PSDLayoutTool2`

### 使用方式

1. 将 `.psd` 文件放到 Unity 项目的 `Assets` 目录下。
2. 在 `Project` 面板选中该 PSD 文件。
3. 在 `Inspector` 中使用 **PSD Layout Tool 2** 的选项和按钮。

主要选项：

- `Maximum Depth`
- `Pixels to Unity Units`
- `Use Unity UI`
- `Output Mode`
- `Output Folder Name`
- `Prefab Output`

主要操作：

- `Export Layers as Textures`
- `Layout in Current Scene`
- `Generate Prefab`

### 特殊标签（与原版标签规则兼容）

#### 组图层标签

- `|Animation`：将子图层作为动画帧生成精灵动画
- `|FPS=##`：设置动画帧率（默认 30）
- `|Button`：将子图层按状态生成按钮

#### 普通图层标签

- `|Disabled`
- `|Highlighted`
- `|Pressed`
- `|Default`
- `|Enabled`
- `|Normal`
- `|Up`
- `|Text`

### 许可证

MIT License，见 [LICENSE.md](LICENSE.md)。

## Unity Version Support

- Verified working on: **Unity 6000.3.7f1**
- The original plugin targets much older Unity versions; this fork adds fixes to run on current Unity.

## What Was Changed Compared to UnityPSDLayoutTool

The following changes were added on top of the original `UnityPSDLayoutTool`:

1. Updated API compatibility for modern Unity editor versions.
2. Fixed PSD Unicode string decoding so Chinese layer names/text import correctly.
3. Added Chinese-friendly font fallback strategy for text import.
4. Added deterministic render ordering to reduce angle-dependent layer overlap issues.
5. Added configurable output folder behavior for generated assets.
6. Added configurable prefab output mode:
   - default: prefab saved as a sibling of the generated output folder
   - optional: prefab saved inside the generated output folder
7. Renamed plugin folder and namespace for this fork:
   - Folder: `Assets/PSDLayoutTool2`
   - Namespace: `PsdLayoutTool2`

## Installation

Copy the folder below into your Unity project:

- `Assets/PSDLayoutTool2`

## Usage

1. Put a `.psd` file under your Unity project's `Assets` directory.
2. Select the PSD file in the Project window.
3. In Inspector, use **PSD Layout Tool 2** options and buttons.

Main options include:

- `Maximum Depth`
- `Pixels to Unity Units`
- `Use Unity UI`
- `Output Mode`
- `Output Folder Name`
- `Prefab Output`

Actions:

- `Export Layers as Textures`
- `Layout in Current Scene`
- `Generate Prefab`

## Special Tags (same as original behavior)

### Group Layer Tags

- `|Animation` : create sprite animation from child layers
- `|FPS=##` : set animation FPS (default 30)
- `|Button` : create button from tagged child layers

### Art Layer Tags

- `|Disabled`
- `|Highlighted`
- `|Pressed`
- `|Default`
- `|Enabled`
- `|Normal`
- `|Up`
- `|Text`

## License

MIT License. See [LICENSE.md](LICENSE.md).

## Credit

This project is based on the original **UnityPSDLayoutTool** and keeps the same MIT license model.
