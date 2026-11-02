import {test} from 'node:test';
import assert from 'node:assert/strict';
import {newPane,panes,splitPane,removePane,moveTab,restoreState} from '../src/layout.ts';
test('split and collapse retain the sibling and every live tab',()=>{
  const first=newPane();first.tabs=['a'];first.activeTabId='a';
  const ws={id:'w',name:'work',path:'D:\\work',pinned:false,tree:first,activePaneId:first.id};
  const second=splitPane(ws,first.id,'horizontal');second.tabs=['b'];second.activeTabId='b';
  const third=splitPane(ws,second.id,'vertical');third.tabs=['c'];third.activeTabId='c';
  assert.deepEqual(panes(ws.tree).flatMap(p=>p.tabs),['a','b','c']);
  ws.tree=removePane(ws.tree,second.id)!;
  assert.deepEqual(panes(ws.tree).flatMap(p=>p.tabs),['a','c']);
});
test('tab transfer and reorder never duplicate a session',()=>{
  const first=newPane();first.tabs=['a','b'];first.activeTabId='b';
  const second=newPane();second.tabs=['c'];second.activeTabId='c';
  const workspaces=[{id:'w',name:'w',path:'D:\\w',pinned:false,tree:first,activePaneId:first.id},{id:'x',name:'x',path:'D:\\x',pinned:false,tree:second,activePaneId:second.id}];
  moveTab(workspaces,'b',second,'c');assert.deepEqual(first.tabs,['a']);assert.deepEqual(second.tabs,['b','c']);assert.equal(first.activeTabId,'a');
  moveTab(workspaces,'c',second,'b');assert.deepEqual(second.tabs,['c','b']);
});
test('corrupt saved layout is rejected while valid preferences are bounded',()=>{
  const result=restoreState({theme:'light',fontSize:80,sidebarWidth:1,workspaces:[{id:'bad',name:'bad',path:'D:\\bad',tree:{type:'split',id:'broken'}} as any]});
  assert.equal(result.workspaces.length,0);assert.equal(result.theme,'light');assert.equal(result.fontSize,24);assert.equal(result.sidebarWidth,190);
});
