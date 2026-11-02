// Adapted from Kooky PaneNode.swift, copyright (c) 2026 kooky contributors, MIT.
// Windows changes: typed JSON tree, immutable split/removal, per-pane tab transfer.
import type { Pane, PaneNode, Workspace, AppState } from './types.ts';
export function newPane():Pane { return {type:'pane',id:crypto.randomUUID(),tabs:[],activeTabId:null}; }
export function panes(node:PaneNode):Pane[] { return node.type==='pane' ? [node] : [...panes(node.first),...panes(node.second)]; }
export function findPane(node:PaneNode,id:string):Pane|undefined { return panes(node).find(p=>p.id===id); }
export function replaceNode(node:PaneNode,id:string,replacement:PaneNode):PaneNode {
  if(node.id===id) return replacement;
  return node.type==='pane' ? node : {...node,first:replaceNode(node.first,id,replacement),second:replaceNode(node.second,id,replacement)};
}
export function splitPane(workspace:Workspace,id:string,orientation:'horizontal'|'vertical'):Pane {
  const pane=findPane(workspace.tree,id);if(!pane)throw new Error('分屏目标不存在');
  const next=newPane();workspace.tree=replaceNode(workspace.tree,id,{type:'split',id:crypto.randomUUID(),orientation,fraction:0.5,first:pane,second:next});workspace.activePaneId=next.id;return next;
}
export function removePane(node:PaneNode,id:string):PaneNode|null {
  if(node.type==='pane')return node.id===id?null:node;
  const first=removePane(node.first,id),second=removePane(node.second,id);
  if(!first)return second;if(!second)return first;return {...node,first,second};
}
export function moveTab(workspaces:Workspace[],sessionId:string,targetPane:Pane,beforeId?:string):void {
  for(const workspace of workspaces)for(const pane of panes(workspace.tree)) {
    const index=pane.tabs.indexOf(sessionId);if(index<0)continue;
    pane.tabs.splice(index,1);if(pane.activeTabId===sessionId)pane.activeTabId=pane.tabs[Math.max(0,index-1)]||null;
  }
  const index=beforeId?targetPane.tabs.indexOf(beforeId):-1;
  targetPane.tabs.splice(index<0?targetPane.tabs.length:index,0,sessionId);targetPane.activeTabId=sessionId;
}
function validNode(value:any,seen:Set<string>,depth=0):value is PaneNode {
  if(!value||typeof value.id!=='string'||seen.has(value.id)||depth>12)return false;seen.add(value.id);
  if(value.type==='pane')return Array.isArray(value.tabs)&&value.tabs.length<200&&value.tabs.every((x:any)=>typeof x==='string')&&(value.activeTabId===null||typeof value.activeTabId==='string');
  return value.type==='split'&&['horizontal','vertical'].includes(value.orientation)&&Number.isFinite(value.fraction)&&value.fraction>0&&value.fraction<1&&validNode(value.first,seen,depth+1)&&validNode(value.second,seen,depth+1);
}
export function restoreState(raw:Partial<AppState>|null):AppState {
  const state:AppState={schemaVersion:1,workspaces:[],activeWorkspaceId:null,recent:[],theme:'dark',fontSize:13,sidebarWidth:240,sessionSnapshots:[]};
  if(!raw||typeof raw!=='object')return state;
  if(raw.theme==='light')state.theme='light';
  if(Number.isFinite(raw.fontSize))state.fontSize=Math.max(10,Math.min(24,raw.fontSize!));
  if(Number.isFinite(raw.sidebarWidth))state.sidebarWidth=Math.max(190,Math.min(380,raw.sidebarWidth!));
  const seen = new Set<string>();
  if(Array.isArray(raw.workspaces))state.workspaces=raw.workspaces.filter(w=>w&&typeof w.id==='string'&&typeof w.path==='string'&&typeof w.name==='string'&&validNode(w.tree,seen)).slice(0,100).map(w=>({...w,pinned:!!w.pinned,activePaneId:findPane(w.tree,w.activePaneId)?.id||panes(w.tree)[0].id}));
  state.activeWorkspaceId=state.workspaces.some(w=>w.id===raw.activeWorkspaceId)?raw.activeWorkspaceId!:state.workspaces[0]?.id||null;
  if(Array.isArray(raw.recent))state.recent=raw.recent.filter(r=>r&&typeof r.path==='string'&&typeof r.name==='string').slice(0,30);
  if(Array.isArray(raw.sessionSnapshots))state.sessionSnapshots=raw.sessionSnapshots.filter(s=>s&&typeof s.id==='string'&&typeof s.cwd==='string'&&typeof s.agentId==='string');
  return state;
}
