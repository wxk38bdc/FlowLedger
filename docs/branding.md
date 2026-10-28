# FlowLedger 品牌资源

圆角方形的深青底色搭配薄荷绿与青色双向流线，表示上传、下载和持续流动的数据；中央三条横线呼应流量账本。图案不包含文字，便于用于小尺寸窗口、任务栏和桌面图标。

## 文件与使用

- `src/FlowLedger.Desktop/Assets/FlowLedger.png`：生成的原始 PNG，1254 × 1254，圆角以外保留透明通道，用于侧栏和 README。
- `src/FlowLedger.Desktop/Assets/FlowLedger.ico`：同一图案的 Windows 图标，包含 16、20、24、32、40、48、64、128、256 像素版本，用于 exe、窗口及桌面快捷方式。

PNG 由 OpenAI 内置 image_gen 工具按 imagegen 技能生成，日期为 2026-10-28。ICO 仅进行尺寸缩放与格式封装，没有重新绘制图案。下方保存实际提交给生成工具的完整提示词，便于后续设计迭代；生成输出具有随机性。

## 生成提示词

```text
Use case: logo-brand
Asset type: Windows desktop application icon for FlowLedger, a network traffic usage ledger.
Primary request: Create one polished rounded-square app logo, straight-on and isolated, with genuinely transparent canvas outside its rounded-square silhouette. Inside a deep midnight teal rounded square, a bold mint and turquoise symbol combines two opposing flowing data ribbons with three ledger-like horizontal strokes, subtly suggesting an F. The emblem communicates incoming/outgoing network flow and organized accounting. Strong simple geometry, generous negative space, even visual weight, highly readable at 32 pixels. Flat vector-like finish, extremely subtle tonal variation only. Centered emblem, rounded square fills about 92% of a square canvas. Corners radius about 22% of tile width.
Constraints: one icon only, no letters or words, no watermark, no surrounding objects, no mockup, no cast shadow outside the tile, no tiny details. Preserve actual alpha transparency beyond rounded corners.
```
