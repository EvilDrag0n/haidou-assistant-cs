// 生成 builds-flat.json：把「专属方案 + 模板 + 兜底」在构建期解析好，
// 让 C# 侧只做字典查表，不用处理嵌套结构和 archetype 逻辑。
const fs = require('node:fs');
const path = require('node:path');

const SRC = path.join(__dirname, '..', 'data');
const table = JSON.parse(fs.readFileSync(path.join(SRC, 'builds.json'), 'utf8'));
const items = JSON.parse(fs.readFileSync(path.join(SRC, 'equipment_name_to_id.json'), 'utf8'));
const nameToKey = JSON.parse(fs.readFileSync(path.join(SRC, 'champion_name_to_key.json'), 'utf8'));

function archetypeOf(name) {
  for (const [key, members] of Object.entries(table.archetype_members || {})) {
    if (members.includes(name)) return key;
  }
  return null;
}

const out = {};
const missing = new Set();

for (const champion of Object.keys(nameToKey)) {
  let builds = table.champion_builds?.[champion];
  let source = '专属方案';
  if (!builds?.length) {
    const arch = archetypeOf(champion);
    const a = arch ? table.archetypes[arch] : null;
    if (a) { builds = [{ play_style: a.play_style, equipment_list: a.items }]; source = '模板 ' + arch; }
  }
  if (!builds?.length) {
    const d = table.archetypes[table.default_archetype];
    builds = [{ play_style: d.play_style, equipment_list: d.items }];
    source = '兜底';
  }

  out[champion] = builds.slice(0, 3).map((b) => {
    const ids = [];
    const names = [];
    for (const n of b.equipment_list || []) {
      const id = items[n];
      if (id) { ids.push(String(id)); names.push(n); }
      else missing.add(n);
    }
    return { title: b.play_style || '', ids, names };
  }).filter((b) => b.ids.length > 0);
}

fs.writeFileSync(path.join(SRC, 'builds-flat.json'), JSON.stringify(out), 'utf8');
console.log('英雄数', Object.keys(out).length, '| 未识别装备', missing.size ? [...missing].join(',') : '无');
console.log('样例', JSON.stringify(out['黑暗之女'], null, 1));
