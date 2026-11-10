const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

for (const name of ['panel.html', 'debug.html', 'hex.html']) {
  const html = fs.readFileSync(path.join(__dirname, '..', 'wwwroot', name), 'utf8');
  const ids = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map(m => m[1]));
  for (const match of html.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/g)) {
    new vm.Script(match[1], { filename: name });
    for (const ref of match[1].matchAll(/\$\(['"]([^'"]+)['"]\)/g)) {
      if (!ids.has(ref[1])) throw new Error(`${name}: missing element ${ref[1]}`);
    }
  }
  if (html.includes('cb-rec') || html.includes('data-act="recommend"')) {
    throw new Error(`${name}: removed recommendation control remains`);
  }
  console.log(`[OK] ${name}: script syntax and element references`);
}
new vm.Script(fs.readFileSync(path.join(__dirname, '..', 'wwwroot', 'hex-view.js'), 'utf8'), { filename: 'hex-view.js' });
console.log('[OK] hex-view.js: shared renderer syntax');
