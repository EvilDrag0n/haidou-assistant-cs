(function(global){
  'use strict';
  const rarityName={silver:'银色',gold:'金色',prismatic:'棱彩'};
  const number=n=>Number(n||0).toLocaleString('zh-CN');
  const percent=n=>Number.isFinite(n)?(n*100).toFixed(1)+'%':'--';
  function element(tag,cls,text){const n=document.createElement(tag);if(cls)n.className=cls;if(text!==undefined)n.textContent=text;return n;}
  function order(rows,min){return (Array.isArray(rows)?rows:[]).filter(r=>Number.isFinite(r.winRate)&&r.sampleCount>=min).slice().sort((a,b)=>b.winRate-a.winRate||b.sampleCount-a.sampleCount||a.id-b.id);}
  function create(root,actions){
    root.classList.add('hex-view');
    const eyebrow=element('div','hex-eyebrow','ARAM MAYHEM / 英雄决策');
    const title=element('h2','hex-title','等待选择英雄');
    const meta=element('p','hex-meta');
    const toolbar=element('div','hex-toolbar');
    function select(label,options){
      const wrap=element('label','',label),input=element('select');input.setAttribute('aria-label',label);
      for(const [value,text] of options){const option=element('option','',text);option.value=value;input.appendChild(option);}
      wrap.appendChild(input);toolbar.appendChild(wrap);return input;
    }
    const dataset=select('统计',[['high','高分段'],['all','全段位']]);
    const stage=select('轮次',[['all','全部'],['1','第 1 轮'],['2','第 2 轮'],['3','第 3 轮'],['4','第 4 轮']]);
    const rarity=select('品质',[['all','全部'],['silver','银色'],['gold','金色'],['prismatic','棱彩']]);
    const minimum=select('样本 ≥',[['100','100'],['500','500'],['1000','1000'],['10','10']]);
    const refresh=element('button','','重新获取');toolbar.appendChild(refresh);
    const tabs=element('div','hex-tabs');tabs.setAttribute('role','tablist');tabs.setAttribute('aria-label','推荐类型');
    const singlesTab=element('button','','推荐海克斯'),combosTab=element('button','','高胜率组合');
    for(const tab of [singlesTab,combosTab])tab.setAttribute('role','tab');tabs.append(singlesTab,combosTab);
    const content=element('div');root.append(eyebrow,title,meta,toolbar,tabs,content);
    let mode='augments';
    let last=null,signature='';
    function hint(text,error){const n=element('div','hex-empty'+(error?' hex-error':''),text);content.appendChild(n);}
    function heading(label,caption){const n=element('div','hex-list-heading',label);n.appendChild(element('small','',caption));content.appendChild(n);}
    function card(row,combo){
      const box=element('article','hex-card'+(combo?' hex-combo':''));
      if(!combo)box.dataset.rarity=row.rarity;
      const line=element('div','hex-card-line');
      line.appendChild(element('div','hex-card-name',combo?row.augments.map(a=>a.name).join(' ＋ '):row.name));
      const rate=element('div','hex-card-rate',percent(row.winRate));rate.appendChild(element('small','','胜率'));line.appendChild(rate);box.appendChild(line);
      const sub=element('div','hex-card-sub');
      if(!combo){sub.appendChild(element('span','hex-pill',(rarityName[row.rarity]||'未知品质')+(row.tier?' · '+row.tier:'')));}
      sub.appendChild(element('span',row.sampleCount<100?'hex-sample-warning':'','样本 '+number(row.sampleCount)+(row.sampleCount<100?' · 小样本':'')));
      if(!combo&&Array.isArray(row.availableStages)&&row.availableStages.length)sub.appendChild(element('span','','可出现：'+row.availableStages.join('/')));
      box.appendChild(sub);
      if(!combo&&row.description){const desc=element('details','hex-description');desc.append(element('summary','','强化效果'),element('div','',row.description));box.appendChild(desc);}
      return box;
    }
    function render(status,force){
      last=status;const data=status.hex||{},cfg=status.config||{};
      const sig=JSON.stringify([data,cfg.hexDataset,cfg.hexMinSamples]);
      if(!force&&signature===sig)return;signature=sig;
      title.textContent=data.champion?data.champion+' · 海克斯':'等待选择英雄';
      dataset.value=cfg.hexDataset==='all'?'all':'high';
      const min=String(cfg.hexMinSamples||100);
      if(![...minimum.options].some(o=>o.value===min)){const o=element('option','',min);o.value=min;minimum.appendChild(o);}
      minimum.value=min;refresh.disabled=data.status==='loading'||!(data.championId>0);content.replaceChildren();
      singlesTab.setAttribute('aria-selected',String(mode==='augments'));combosTab.setAttribute('aria-selected',String(mode==='combinations'));
      stage.disabled=rarity.disabled=mode==='combinations';
      const ds=data.dataset==='all'?'全段位':'高分段';
      meta.textContent='来源 ARAMKit · '+ds+' · 选定英雄后实时联网查询';
      if(data.status==='loading'){hint('正在查询最新版本与当前英雄统计…\n更换英雄后不会展示上一位的推荐。');return;}
      if(data.status==='error'){hint(data.error||'获取失败，请重新获取。',true);return;}
      if(data.status==='idle'||!data.status){hint('进入海克斯选人阶段并选定英雄后，\n这里自动显示推荐海克斯和高胜率组合。');return;}
      meta.textContent='ARAMKit · '+ds+' · 版本 '+data.version+'\n数据发布 '+(data.dataDate||'未提供')+' · 获取 '+(data.fetchedAt?new Date(data.fetchedAt).toLocaleTimeString('zh-CN',{hour12:false}):'--');
      if(data.championSamples)content.appendChild(element('div','hex-baseline','英雄样本 '+number(data.championSamples)+' 场 · 英雄胜率 '+percent(data.championWinRate)));
      if(data.status==='empty'){hint('数据源暂未提供该英雄的统计。');return;}
      if(mode==='augments'){
      let candidates=stage.value==='all'?data.augments:((data.stages||{})[stage.value]||[]);
      if(stage.value!=='all'){
        const ids=new Set(candidates.map(r=>r.id));
        candidates=candidates.concat((data.augments||[]).filter(r=>r.stageAgnostic&&!ids.has(r.id)));
      }
      const rows=order(candidates,Number(minimum.value)).filter(r=>rarity.value==='all'||r.rarity===rarity.value).slice(0,6);
      heading('推荐海克斯','按胜率排序 · 当前筛选');
      const singles=element('div','hex-cards');for(const row of rows)singles.appendChild(card(row,false));content.appendChild(singles);
      if(!rows.length)hint('该筛选下没有足够样本，可调整轮次、品质或样本门槛。');
      }else{
      const combos=order(data.combinations,Number(minimum.value)).slice(0,5);
      heading('高胜率组合','全轮次 · 全品质');
      const pairs=element('div','hex-cards');for(const row of combos)pairs.appendChild(card(row,true));content.appendChild(pairs);
      if(!combos.length)hint('数据源已发布的组合中，没有达到当前样本门槛的记录。');
      content.appendChild(element('div','hex-note','组合使用该英雄的实际共同选取统计，不将单个强化胜率相乘。列表仅覆盖数据源已发布的组合；统计胜率不代表必胜。'));
      }
    }
    stage.onchange=rarity.onchange=()=>{if(last)render(last,true);};
    singlesTab.onclick=()=>{mode='augments';if(last)render(last,true);};
    combosTab.onclick=()=>{mode='combinations';if(last)render(last,true);};
    async function update(patch){
      try{await actions.setConfig(patch);}catch(e){hint('设置失败：'+e.message,true);}
    }
    dataset.onchange=()=>update({hexDataset:dataset.value});
    minimum.onchange=()=>update({hexMinSamples:Number(minimum.value)});
    refresh.onclick=async()=>{refresh.disabled=true;try{await actions.refresh();}catch(e){hint('获取失败：'+e.message,true);}finally{if(last)render(last,true);}};
    return{render};
  }
  global.HexView={create,order,percent};
})(window);
