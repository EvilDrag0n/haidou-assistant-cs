# ARAMKit 在线数据 · 1.3.0 已接入（2026-10-30）

本节为当前实现；下方 2026-10-29 内容是历史调研，缓存及旧数据降级方案未采用，以用户每次选英雄实时获取的要求为准。

每次选定或更换英雄时 GET https://data.aramkit.cn/data/versions.json，严格选择 latest 版本，读取 dataPath、resourcePath，然后并行请求：

- https://data.aramkit.cn/{dataPath}/stats/{high|all}/champion-details/{championId}.json
- https://data.aramkit.cn/{resourcePath}/zh-CN/resources/augments.json

国内端点失败后通过 https://data.aramkit.com/ 重新查询。每次均取新索引与资源，带禁止缓存策略、请求头和时间戳参数，不读写本地推荐缓存，不降级到旧版本。

统计使用 augments.all / augments.stages（1～4），真实组合来自 augmentCombinations；winRate 为 0～1，sampleCount 为样本量。组合仅覆盖数据源发布的条目，目前每英雄 10 个。英雄整体统计来自 champion.stats；dataDate 是发布日，fetchedAt 是本次获取时间，两者分开展示。

正式程序联网验证：16.21 / 2026-10-30，镕铁少女高分段 127 个强化和 10 个组合，九尾妖狐全段位 138 个强化和 10 个组合。数据路径带版本小数点，不能硬编码路径。接口错误进诊断 status.hex.details，显示重试入口。

代码见 src/HexData.cs；复验见 tools/verify-hex-live.ps1；交付说明见 RELEASE-1.3.0.md。

---

# 以下为历史调研（2026-10-29）

调研时间：2026-10-29　结论：**有可用数据源，但需要再逆向一层 CDN 路径**；本轮先不接入。

## 一、候选站点（都可从国内访问）

| 站点 | 内容 | 国内可达 | 备注 |
|---|---|---|---|
| [ARAMKit 中文站](https://aramkit.cn/zh-CN/augments/reload?dataset=high) | 海克斯强化符文：胜率 / 选取率 / 适配英雄，可按版本与段位筛选 | ✅ 200 | 有明确的 JSON 数据源（见下），最优先 |
| [ARAMKit 国际站](https://aramkit.com/en-US?version=16.18&dataset=high) | 同上（英文） | ✅ 200 | 数据结构与中文站同源 |
| [ARAMGG](https://aramgg.com/en) | 海克斯胜率排行，另有桌面客户端 | ✅ 200 | 也有客户端，可参考其数据接口 |
| [ARAMGG Client 源码](https://github.com/valkia/aramgg_client) | 开源：只读 LCU + 海克斯统计 + 游戏内 OCR 浮层 | — | 可借鉴它的"选完英雄推荐海克斯"实现方式 |
| [apexlol.info](https://apexlol.info/zh-Hant/tier-list) | 海克斯 Tier List | ❌ 403 | 有 Cloudflare 拦截 |
| [aramhex（b10 站同名工具）](https://github.com/czgreat/aramhex) | 开源"海克斯大乱斗助手"：选英雄看胜率、三选一按海克斯胜率推荐 | — | 与本工具定位最接近，可直接读代码 |
| [NGA 相关帖](https://ngabbs.com/read.php?tid=47155596) | 玩家自建数据站 | ✅ | 非结构化 |

## 二、已验证接口（ARAMKit）

从页面 `window.__NUXT__.config` 里拿到数据端点，并实测可用：

```
GET https://data.aramkit.cn/data/versions.json      → 200
{
  "latest": "16.21",
  "versions": [
    { "version": "16.21",
      "dataPath": "data/16.21-20261028-630a0df07f2a",
      "resourcePath": "resources/16.21-32001640c0a4",
      "dataDate": "2026-10-28",
      "allMatches": 23177263,
      "highMatches": 5794316 }
  ]
}
```

- 国际站同结构：`https://data.aramkit.com/data/versions.json`
- 配置里还暴露了评级接口：`https://api.aramkit.cn`、`https://api.aramkit.com`

**未打通的一步**：`<dataPath>/augment-rankings.json`、`<dataPath>/champion-rankings.json`、
`<resourcePath>/resources/augments.json` 等一律 404（返回的是 Cloudflare 的 SPA 404 页）。
说明静态文件的实际路径还带一层前缀（前端代码里是 `endpoint + "/" + dataPath + "/" + 文件名`，
但线上显然还有重写规则）。要么继续扒 `/_nuxt/*.js` 里剩下的路由前缀，要么直接抓页面渲染后的数据。

## 三、接入方案（下一轮做）

### 方案 A：继续逆向 ARAMKit（推荐，数据质量最好）

1. 继续从 `/_nuxt/*.js` 里找数据前缀（已确认存在 `data/versions.json` 这个能用的锚点）；
2. 拿到 `augment-rankings.json` 后按版本缓存到 `hxdou-data/hexdata/`，24h 过期；
3. 面板新增「海克斯」页：按胜率排序、可按"选中的英雄"过滤；
4. 选人阶段拿到英雄后，取该英雄适配的前 3 个海克斯，在浮层/面板提示。

数据结构（从 JS 反推）：`augment-rankings.json` → `{ rows: [{ id, all, stages: {...} }], hasData }`，
每行可按 `dataset=high`（高分段）或全段位取值。

### 方案 B：读 aramhex 源码

`czgreat/aramhex` 是同类开源工具，直接看它用什么数据源（大概率也是 ARAMKit 或自建爬虫），
能省掉逆向时间。

### 方案 C：只用「本地对局数据」

不进网络，只统计**自己打过的局**（LCU `/lol-match-history/v1/products/lol/...` 里有 ARAM 对局，
但拿不到海克斯三选一的具体选项）。只能给"我的历史胜率"，做不了版本 meta。

## 四、风险

- 第三方站点随时改路径/加反爬，需内置多个候选 + 失败静默降级（不弹错、不影响主功能）；
- 数据量大（全段位 × 全海克斯），必须缓存，且只取当前版本；
- 若走"抓页面渲染结果"，需要内嵌浏览器，成本高——优先用 JSON。

## 五、给用户的建议

先按方案 B 读一遍开源实现（半小时），拿不到再走方案 A 逆向。若两周内跑不通，
就退到"本地历史 + 手动导入一份海克斯胜率表"，保证功能不断。
