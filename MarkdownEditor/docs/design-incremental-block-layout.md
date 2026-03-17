# 块级增量布局设计（类 DOM 局部更新）

## 目标123

- **各块独立计算高度**，总高 = 各块高度之和，始终是“实际高度”，不再混用估计总高。
- **检测“当前块是否变更”**，只对变更块失效缓存并重新布局，未变块复用。
- **类似浏览器 HTML 渲染**：DOM 只优化局部，避免全量重算。

---

## 现状简述

- 解析得到 `_cachedBlocks`（AST）+ `_cachedBlockRanges`（行范围）。
- 要么全量布局（所有块），要么按视口做增量布局（只布局视口窗口内的块，窗口外用估计高度累加）。
- 估计高度与真实高度不一致会导致总高/滚动条跳动和点击错位；文档变更后若不做一次全量布局又会偏移。

---

## 核心思路

1. **块级缓存**：每个块有“高度缓存”和可选的“布局缓存”（用于绘制）。
2. **变更检测**：文档更新后，对块列表做 diff，只标记**发生变化的块**。
3. **总高 = Σ 块高**：总高度始终由各块（缓存或重算）高度求和得到，不再用“整文档估计总高”。
4. **按需布局**：只为“可见块”做完整布局（产出 runs/lines）；非可见块若失效则只算高度（或做一次 layout 只取 height），不保留完整 layout 以省内存。

---

## 数据结构（建议）

```text
// 块级缓存项
BlockCacheEntry {
    int BlockIndex;
    int StartLine, EndLine;
    string ContentHash;           // 该块源码的短哈希，用于变更检测
    float CachedHeight;           // 实际测量高度，NaN 表示未缓存/已失效
    LayoutBlock? CachedLayout;    // 完整布局，仅可见或最近可见的块保留
    bool HeightInvalidated;       // 内容或宽度变更后置 true
}

// 引擎内维护
List<BlockCacheEntry> _blockCache;   // 与当前 _cachedBlocks 一一对应
float _totalHeight;                  // = Σ CachedHeight + padding，无“估计总高”
int[] _cumulativeY;                  // 或 float[]，Y[i] = 块 0..i-1 的高度和，便于二分查可见区间
```

- 文档或宽度变更时：对块做 diff，只把**内容或行范围变化**的块标记为 `HeightInvalidated`（并清空 `CachedHeight`/`CachedLayout`）。
- 若块数、顺序变化（插入/删除块）：对应索引后的 `BlockCacheEntry` 可整体失效或做索引平移（类似 DOM 的 list diff）。

---

## 变更检测（Doc 更新后）

- **输入**：上一帧的 `_cachedBlocks` + `_cachedBlockRanges` + 每块的 content hash（若已存）；当前帧解析得到的 new blocks + new ranges。
- **策略**：
  - 若文档引用变了（当前实现里是 `ReferenceEquals(doc, _cachedDoc)`），则视为 doc 变更。
  - 重新解析得到 new blocks + new ranges 后，与上一帧逐块比较：
    - **按块索引**：块数不同则可从 0 起对共同长度逐块比较；或做简单 LCS/diff，只对“相同位置且 hash 相同”的块保留缓存。
    - **块内容**：对每块取 `GetSpanText(doc, span)` 的哈希（如 SHA256 截断或 FNV），与 `BlockCacheEntry.ContentHash` 比较；不同则该块 invalidate。
  - 若宽度 `_width` 变了，所有块高度/布局都应失效（或只清高度，布局按需重算）。

这样即可实现“**只对变更块失效**”，未变块保留 `CachedHeight`（以及若在缓存窗口内的 `CachedLayout`）。

---

## 总高度与累积 Y

- **总高度**：
  - `_totalHeight = ExtraBottomPadding + Σ blockHeight(i)`，
  - 其中 `blockHeight(i)` = 若 `BlockCacheEntry[i]` 有效则用 `CachedHeight`，否则**仅对该块做一次 layout 取 Bounds.Height**（不保留 runs，只留高度），再写入缓存。
- 这样**总高始终由各块实际高度聚合**，不再有“估计总高”与“实际总高”的切换，滚动条和 MeasureTotalHeight 稳定。
- **累积 Y**：维护 `Y[0]=0`, `Y[i]=Y[i-1]+blockHeight(i-1)`，便于：
  - 给定 scrollY / viewportHeight，二分或线性找到 `[startBlock, endBlock)`；
  - HitTest 时用 `Y[blockIndex]` 与块内行偏移得到 contentY。

---

## 按需布局（只对可见块做完整 layout）

- **可见区间**：用 `_cumulativeY` + scrollY、viewportHeight 算出 `[startBlock, endBlock)`。
- **对块 i ∈ [startBlock, endBlock)**：
  - 若 `CachedLayout` 有效且未 invalidate，直接复用；
  - 否则对该块调用现有 `_layout.Layout(ast, contentWidth, blockIndex, startLine, endLine)`，得到 `LayoutBlock`，写入 `CachedLayout`，并用 `Bounds.Height` 更新 `CachedHeight` 与 `_totalHeight` / `_cumulativeY`。
- **对块 i 不在可见区间内**：
  - 若该块已 invalidate 且 `CachedHeight` 未设置：可**只做一次 layout 取高度**（或做完整 layout 但只保留 Bounds.Height 后丢弃 runs），更新 `CachedHeight` 和总高；
  - 若该块未 invalidate：直接使用已有 `CachedHeight`，不 layout。

这样“**各块各自计算各自的高度**”，且“**仅变更块 + 仅可见块**做 layout”，其余复用，类似 DOM 只更新 dirty 节点。

---

## 与当前实现的对应关系

| 当前 | 建议 |
|------|------|
| 全量布局 vs 增量布局（按视口窗口） | 保留“全量”作为“全部块都算一遍高度/布局”的路径；日常用块级缓存 + 变更检测 + 只算失效块与可见块 |
| `_cachedEstimatedTotalHeight` vs `_cachedTotalHeight` | 取消估计总高；`_totalHeight` 始终由 Σ 块高得到 |
| 文档变更后清空所有缓存 | 文档变更后做块 diff，只 invalidate 变更块 |
| 增量布局用估计 Y 求窗口 | 用各块实际高度求累积 Y，再求可见窗口 |

---

## 实现步骤（可渐进）

1. **块级缓存与 diff**  
   在解析后得到 new blocks + ranges，与上一帧比较，为每个块维护 `BlockCacheEntry`（含 ContentHash、CachedHeight、CachedLayout、Invalidated）。
2. **总高 = Σ 块高**  
   实现“取块高”：若缓存有效用缓存，否则 layout 该块取 Bounds.Height 并写回；总高 = sum(块高) + padding；MeasureTotalHeight 直接返回该总高。
3. **累积 Y 与可见区间**  
   用块高数组维护 cumulativeY，实现 GetVisibleBlockRange 基于实际高度。
4. **只对可见块保留完整 Layout**  
   可见块做完整 layout 并缓存；非可见块若失效只算高度不保留 runs，减少内存。
5. **移除“估计总高”与“估计/实际切换”**  
   删除 `_cachedEstimatedTotalHeight` 和依赖它的逻辑，统一用块级实际高度聚合。

这样即可实现“**AST 后面的块各自计算各自的高度，同时检测当前块是否更改**”，整体行为类似浏览器对 DOM 的局部更新与布局。
