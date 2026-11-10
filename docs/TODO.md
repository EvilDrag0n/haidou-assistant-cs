# 1.3.0 本轮状态（2026-10-30）

- [x] 按最新版本接入 ARAMKit 英雄海克斯和真实组合统计
- [x] 每次换英雄、换统计段位和手动刷新重新联网；不使用本地推荐缓存
- [x] 加载/失败即清空旧推荐，慢响应不覆盖新英雄
- [x] 英雄选择侧窗，支持左右吸附、屏幕边界、前台跟随和关闭后抑制
- [x] 主面板信息前置、四位队友首屏展示、参数折叠和战绩提示修正
- [x] C# 5 编译及 21 项自检通过，页面脚本检查通过
- [x] 正式取数代码联网实测两个英雄与两个统计段位
- [x] WebView2 13 项界面检查和截图检查通过
- [x] 更新文档、上机验证清单并生成 1.3.0 交付物
- [ ] 真实游戏选人界面的吸附、换人及前台切换仍需上机验证

详见 RELEASE-1.3.0.md。下方保留 1.2.0 及更早历史。

---

# 1.2.0 本轮状态（2026-10-30）

- [x] 修复轮询定时器生命周期、自动接受及待接受状态诊断
- [x] 修复 Tab 持续刷新，记录实际 2999 错误和原始装备价格
- [x] 按既有约定移除推荐写入，仅留旧文件清理
- [x] 战绩逐队友发送，简化格式并记录逐条结果
- [x] 15 项自检通过；界面脚本检查通过
- [ ] 本轮真实客户端上机确认（当前未提供新诊断 JSON）

最新说明：RELEASE-1.2.0.md；上机步骤：VERIFY-CHECKLIST.md。

---

以下为 1.1 的历史待办记录，“已修复”指当时的实现状态。
# 海斗助手 C# 版 · 本轮待办

> 依据：`hxdou-cs-diag-20261029-1525.json`（2026-10-29 15:25 导出于 D:\lol-helper）
> 完成一项 → 跑一次 `test.bat` + 必要的实测 → 打勾

## A. 五个缺陷

- [x] **A1 自动接受不生效**
  - 现状：`autoAccept.enabled=true`、`armed=false`，从未发出接受
  - 已定位：`ReadyStep` 用 `lastKnownQueue` 判白名单，而匹配中/待接受阶段 gameflow 里 `gameData.queue` 为空 → `lastKnownQueue=0` → 白名单判定失败
  - 另一个 bug：`acceptedThisGame` 置 true 后永不复位，第 2 局起永远不再接受
  - 修法：队列号从 `/lol-lobby/v2/lobby`、`/lol-matchmaking/v1/ready-check` 补取；0 视为未知→放行；离开 ReadyCheck 复位
- [x] **A2 队友战绩取不到（面板显示"战绩不可见"）**
  - 已定位：`JavaScriptSerializer.MaxJsonLength` 默认 2 MB，`/lol-match-history/v1/products/lol/{puuid}/matches` 的响应远超 → 抛异常 → 两个 URL 都失败（诊断里 3 个队友 6 次请求正是这个特征）
  - 修法：全局 `MaxJsonLength = int.MaxValue`；并补 `participantIdentities` 老结构兜底
- [x] **A3 按住 Tab 不显示经济差**
  - 现状：`gold.data=null`、`error="等待对局数据"`（导出时不在对局中，无法证明）
  - 已定位的可疑点：`inGame` 只看 phase；2999 未连通时静默失败无痕迹
  - 修法：gold 不再强依赖 phase（只要 2999 能取到数据就显示）；`gold.error/updatedAt` 写进状态便于下轮定位；按住 Tab 时若还没数据会就地补取一次
- [x] **A4 折叠后没有托盘图标**
  - 修法：托盘/窗口统一用 `app.ico`；最小化即收进托盘；首次提示气泡；托盘菜单加"退出"
- [x] **A5 商城推荐出装无效**
  - 现状：`recommendedCount=0`（导出时已离开选人，无法证明）
  - 已定位的可疑点：只在"选到英雄那一刻"写文件，客户端商店可能已加载完推荐表不再刷新；目录只写了一个候选
  - 修法（按你的选择：保持"选到英雄时写"，不预写全部）：① 同时写 `Game\Config\Global\Recommended` 与 `Config\Global\Recommended` 两个候选目录；② item set 补 `associatedChampions`/`associatedMaps`/`startedFrom`；③ 调试页加「检查推荐出装」，显示实际目录、文件数，并可强制重写一次用于现场验证

## B. 三项功能调整

- [x] **B1 自动接受的随机延迟可配置**（面板两个输入框：最小/最大毫秒，留空用默认 800~1500）
- [x] **B2 马评胜率阈值可配置**（上等/中等/下等三档下限，默认 80/50/30，校验单调递增）
- [x] **B3 国服可用的海克斯数据源调研**（已完成调研，接入待定，见 `docs/HEX-DATA-RESEARCH.md`）
  - 结论：ARAMKit 中文站可访问，`https://data.aramkit.cn/data/versions.json` 实测 200，但榜单文件路径还需再逆一层
  - 另找到开源同类工具 `czgreat/aramhex`、`valkia/aramgg_client`，可直接读实现
  - 接入（拉取 → 缓存 → 面板展示 → 选英雄后推荐）留到下一轮

## C. 回归与交付

- [x] C1 `test.bat` 自检覆盖本轮改动（阈值边界含自定义、出装多目录写入、组包序号）
- [x] C2 本地跑通：面板/调试页/浮层/托盘
- [x] C3 更新 `README.md` + 交付目录 `海斗助手-CS版`
- [x] C4 输出一份"上机验证清单"（你在游戏机上按顺序点哪几个按钮、看什么现象）
