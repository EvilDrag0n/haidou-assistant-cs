# 历史更新记录

当前版本见 [RELEASE-1.3.0.md](RELEASE-1.3.0.md)，主 README 在仓库根目录。

## 1.2.0（2026-10-30）

修复轮询 Timer 生命周期、自动接受状态判断与重新匹配复位、Tab 浮层持续刷新和红方视角；移除无效的推荐出装。战绩改为每位队友单独发送一条。15 项自检及界面脚本检查通过；真实对局待上机确认。详见 [RELEASE-1.2.0.md](RELEASE-1.2.0.md)，上机按 [VERIFY-CHECKLIST.md](VERIFY-CHECKLIST.md) 验证。

## 1.1（2026-10-29）：5 个缺陷 + 2 项设置

| 问题 | 根因 | 修法 |
|---|---|---|
| 自动接受不生效 | 匹配中/待接受阶段 gameflow 里没有 `gameData.queue`，`lastKnownQueue=0` → 白名单判定必失败；另外 `acceptedThisGame` 永不复位，第二局起不再接受 | 队列号依次从 gameflow → `/lol-lobby/v2/lobby` → `/lol-matchmaking/v1/ready-check` 取；取不到（0）按未知放行；离开 ReadyCheck 复位 |
| 队友战绩全"不可见" | `JavaScriptSerializer.MaxJsonLength` 默认 2 MB，战绩响应超限直接抛异常 | 放开到 `int.MaxValue`；补 `participantIdentities` 老结构匹配；三种响应形状都吃 |
| Tab 不显示经济差 | 取数强依赖 `phase==InProgress`，切阶段瞬间就失效；失败无痕迹 | 只要 2999 有数据就显示；`gold.error/updatedAt` 进状态；按住 Tab 时若无数据就地补取 |
| 折叠后没有托盘图标 | 托盘用的是系统默认图标，且 Win11 会折叠进溢出区 | 托盘/窗口统一用 `app.ico`；最小化=收进托盘；再次双击 exe 即可唤回面板 |
| 商城推荐出装无效 | 只写一个候选目录；item set 缺 `associatedChampions` | 两个候选目录都写；补 `associatedChampions/associatedMaps/startedFrom`；调试页新增「检查推荐出装」。1.2.0 起改为移除该功能 |

### 1.1 新增设置（面板「参数设置」）

| 项 | 默认 | 说明 |
|---|---|---|
| 接受延迟 | 800 ~ 1500 ms | 自动接受前的随机等待，留空用默认 |
| 上等马 ≥ | 70% | 阈值必须严格递减，保存时校验 |
| 中等马 ≥ | 40% | 40~69% |
| 下等马 ≥ | 20% | 20~39%，低于 20% 为劣等马 |

改完失焦/回车即保存，并立即按已有胜率重算评级（不重新请求战绩）。

## 调试页（16 个按钮，全部可用）

调试页开在**程序内的独立窗口**（同一套 WebView2 桥），不是系统浏览器。

| 分组 | 按钮 | 说明 |
|---|---|---|
| 按顺序点 1–6 | 检查连接 / 转储选人数据 / 探测队友接口 / 对局内数据(2999) / 待接受状态(只读) / 导出诊断 | 全部只读，输出逐条列出成功/404/失败 |
| 单独工具 | 运行环境自检 / 定位 LoL 安装目录 / 发送测试消息到选人聊天 / 导出接口清单(swagger) / 检查推荐出装 / 全局接口探测 / 预览经济差浮层 / 清理出装文件 / 打开探测输出目录 / 立即接受（唯一会改状态的） | 每个都会把结果贴到右侧，可复制 |

> 「检查推荐出装」随 1.2.0 移除推荐出装功能后已下线，此表保留用于回溯旧版本。

## 迁移初版背景

本项目由 Electron 版迁移而来，界面（`wwwroot\panel.html` 等）与 Electron 版是同一套 HTML/CSS/JS，靠注入的 `window.hx` 桥接驱动，接口签名一致；`data\builds-flat.json` 由 `tools\gen-builds-flat.js` 在构建期把"专属方案 → 模板 → 兜底"解析好，C# 只做查表；配置字段完全一致，可直接把 Electron 版的 `config.json` 拷过来用。1.3.0 起增加共享海克斯组件（`wwwroot\hex-view.js` + `hex.css`），主面板与选人侧窗共用同一套渲染。
