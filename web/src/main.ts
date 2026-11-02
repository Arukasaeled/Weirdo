import '@xterm/xterm/css/xterm.css';
import './style.css';
import {call,isDesktop,on} from './bridge';
import {findPane,moveTab,newPane,panes,removePane,restoreState,splitPane} from './layout';
import {Terminals} from './terminal';
import {FileTreePanel} from './file-tree';
import {chooseWorkspace,type WorkspaceLocation} from './workspace-chooser';
import {button,closeMenu,confirm,dialog,el,icon,input,labeledButton,menu,prompt,toast,type MenuItem} from './ui';
import type {AgentProfile,AppState,Bootstrap,Chunk,NativeSession,Pane,PaneNode,SessionInfo,Workspace,WorkspaceInfo} from './types';

const app=document.querySelector<HTMLDivElement>('#app')!;
const monitorMode=new URLSearchParams(location.search).has('monitor');
const sessions=new Map<string,SessionInfo>();
const liveIds=new Set<string>();
const resumingSessions=new Set<string>();
const workspaceInfo=new Map<string,WorkspaceInfo>();
const workspaceInspections=new Map<string,Promise<WorkspaceInfo>>();
const workspaceInspectionErrors=new Map<string,string>();
let state:AppState=restoreState(null),agents:AgentProfile[]=[],appVersion='0.2.1',homePath='',booted=false;
let fileMode=false,sidebarCollapsed=false,zoomPaneId:string|null=null,saveTimer:ReturnType<typeof setTimeout>|undefined;
let inspectionId=0,renderScheduled=false;
let maximized = false;
let connectionActive = isDesktop;
const parking=el('div','terminal-parking');parking.setAttribute('aria-hidden','true');
const topbar=el('header','topbar'),sidebar=el('aside','sidebar'),content=el('main','workspace-content'),statusbar=el('footer','statusbar'),workspaceTitle=el('span','workspace-title');
const sidebarContent=el('div','sidebar-content'),sidebarHeader=el('div','sidebar-header'),sidebarFooter=el('div','sidebar-footer');
const shell=el('div','app-shell'),body=el('div','body');
const error=(value:unknown)=>toast(value instanceof Error?value.message:String(value),true);
const run=(action:()=>Promise<unknown>)=>{void action().catch(error);};
const terminals=new Terminals(parking,error,text=>confirm('粘贴多行内容',`此终端未启用括号粘贴。换行可能立即执行命令，是否继续？\n\n${text.slice(0,600)}${text.length>600?'\n…':''}`,'粘贴'),onOutput);
const fileTree=new FileTreePanel({
  canInsert:()=>Boolean(activeSession()?.running),
  insert:insertPath,
  quote:quotePath,
  error
});

function logoImage(className='brand-logo'):HTMLImageElement {
  const image=el('img',className);
  image.src='/branding/weirdo-logo.png';
  image.alt=className==='welcome-logo'?'Weirdo':'';
  image.draggable=false;
  return image;
}

function activeWorkspace(){return state.workspaces.find(w=>w.id===state.activeWorkspaceId);}
function activePane(){const workspace=activeWorkspace();return workspace?findPane(workspace.tree,workspace.activePaneId)||panes(workspace.tree)[0]:undefined;}
function activeSession(){const id=activePane()?.activeTabId;return id?sessions.get(id):undefined;}
function agentName(id:string){return agents.find(a=>a.id===id)?.name||id;}
function baseName(path:string){return path.replace(/[\\/]+$/,'').split(/[\\/]/).pop()||path;}
function compactPath(path:string){return homePath&&path.toLowerCase().startsWith(homePath.toLowerCase())?`~${path.slice(homePath.length)}`:path;}
function workspaceKey(path:string){return path.replace(/\\/g,'/').replace(/\/+$/,'').toLowerCase();}
function panePath(pane:Pane,workspace:Workspace){return (pane.activeTabId?sessions.get(pane.activeTabId)?.cwd:undefined)||workspace.path;}
function activity(session:SessionInfo){if(!liveIds.has(session.id))return {label:'待恢复',kind:'disconnected'};if(!session.running)return {label:session.exitCode==null?'已停止':`已退出 · ${session.exitCode}`,kind:session.exitCode?'failed':'exited'};if(session.lastOutputAt&&Date.now()-Date.parse(session.lastOutputAt)<2500)return {label:'有输出',kind:'output'};return {label:'运行中',kind:'running'};}
function agentIcon(id:string){const node=el('span','agent-icon');const profile=agents.find(a=>a.id===id);const kind=profile?.kind||id;const filename=kind==='claude'||id==='claude'?'claudecode':kind==='codex'||id==='codex'?'codex':kind==='gemini'||id==='gemini'?'gemini':null;if(filename){const img=el('img');img.src=`/icons/${filename}.png`;img.alt='';node.append(img);}else node.append(icon('terminal'));return node;}
function stateSnapshot():AppState{return {...state,sessionSnapshots:[...new Set(state.workspaces.flatMap(w=>panes(w.tree).flatMap(p=>p.tabs)))].flatMap(id=>sessions.has(id)?[sessions.get(id)!]:[])};}
function save(){
  if(!booted||monitorMode)return;
  clearTimeout(saveTimer);
  saveTimer=setTimeout(()=>{
    saveTimer=undefined;
    void call('state.save',stateSnapshot()).catch(error);
  },180);
}

async function flushPendingSave(){
  if(saveTimer===undefined)return;
  clearTimeout(saveTimer);
  saveTimer=undefined;
  await call('state.save',stateSnapshot());
}
function applyTheme(){
  document.documentElement.dataset.theme=state.theme;
  document.documentElement.style.setProperty('--sidebar-width',`${state.sidebarWidth}px`);
  terminals.configure(state.theme,state.fontSize);
  if(isDesktop)void call('window.theme',{theme:state.theme}).catch(error);
}
function scheduleRender(){if(renderScheduled)return;renderScheduled=true;requestAnimationFrame(()=>{renderScheduled=false;render();});}

function setMaximized(value:boolean) {
  maximized = value;
  for (const control of document.querySelectorAll<HTMLButtonElement>('[data-window-maximize]')) {
    const label = maximized ? '还原窗口' : '最大化窗口';
    control.title = label;
    control.setAttribute('aria-label', label);
    control.replaceChildren(icon(maximized ? 'restore' : 'maximize', 14));
  }
}

async function refreshWindowState() {
  setMaximized(await call<boolean>('window.isMaximized'));
}

async function toggleMaximized() {
  setMaximized(await call<boolean>('window.maximize'));
}

function windowControls(): HTMLElement {
  const controls = el('div', 'window-controls');
  controls.setAttribute('aria-label', '窗口控制');
  const minimize = button('最小化窗口', () => run(() => call('window.minimize')), 'minimize', 'window-control');
  const maximize = button(maximized ? '还原窗口' : '最大化窗口', () => run(toggleMaximized), maximized ? 'restore' : 'maximize', 'window-control');
  maximize.dataset.windowMaximize = '';
  const close = button(monitorMode ? '关闭活动监视器' : '关闭到托盘，保留终端', () => run(async () => {
    if (booted && !monitorMode) await flushPendingSave();
    await call('window.hide');
  }), 'close', 'window-control window-close');
  controls.append(minimize, maximize, close);
  return controls;
}

function bindTitlebar(bar: HTMLElement) {
  bar.addEventListener('mousedown', event => {
    const target = event.target as Element;
    if (event.button !== 0 || target.closest('button, input, select, textarea, a, [role="button"]')) return;
    event.preventDefault();
    if (event.detail === 2) run(toggleMaximized);
    else if (event.detail === 1) run(() => call('window.drag'));
  });
}

function startupTitlebar():HTMLElement {
  const bar=el('header','startup-titlebar');
  const brand=el('span','startup-brand');brand.append(logoImage(),el('span','','Weirdo'));
  bar.append(brand,windowControls());
  bindTitlebar(bar);
  return bar;
}

function setupShell(){
  const logo=el('div','brand');logo.append(logoImage(),el('strong','','Weirdo'),el('span','platform-tag','WINDOWS'));
  const sidebarToggle=button('切换侧边栏',()=>{sidebarCollapsed=!sidebarCollapsed;shell.classList.toggle('sidebar-collapsed',sidebarCollapsed);terminals.fitAll();},'sidebar','icon-button');
  const search=labeledButton('搜索项目、终端、Agent…',openQuickOpen,'search','quick-open');search.append(el('kbd','','Ctrl P'));
  const tools=el('div','toolbar-actions');tools.append(button('新窗口',()=>run(()=>call('window.new')),'plus','icon-button'),button('Agent 活动监视器',()=>run(()=>call('window.monitor')),'monitor','icon-button'),button('外观与 Agent 设置',openSettings,'settings','icon-button'));
  topbar.append(logo,sidebarToggle,search,tools,windowControls());
  bindTitlebar(topbar);
  sidebarHeader.append(el('span','section-label','工作区'),button('打开项目 · Ctrl Shift O',()=>run(()=>openWorkspace()),'plus','icon-button'));
  const workspaceToggle=labeledButton('项目',()=>{fileMode=false;renderSidebar();},'folder','footer-tab active');workspaceToggle.dataset.sidebarMode='projects';
  const filesToggle=labeledButton('文件',()=>{fileMode=true;renderSidebar();},'file','footer-tab');filesToggle.dataset.sidebarMode='files';
  sidebarFooter.append(workspaceToggle,filesToggle,button('最近打开的项目',openRecent,'history','icon-button'));
  sidebar.append(sidebarHeader,sidebarContent,sidebarFooter);
  const splitter=el('div','sidebar-resizer');splitter.setAttribute('role','separator');splitter.setAttribute('aria-label','调整侧边栏宽度');splitter.setAttribute('aria-orientation','vertical');splitter.tabIndex=0;
  splitter.addEventListener('pointerdown',event=>{event.preventDefault();splitter.setPointerCapture(event.pointerId);const move=(e:PointerEvent)=>{state.sidebarWidth=Math.max(190,Math.min(380,e.clientX));document.documentElement.style.setProperty('--sidebar-width',`${state.sidebarWidth}px`);terminals.fitAll();};const finish=()=>{splitter.removeEventListener('pointermove',move);save();};splitter.addEventListener('pointermove',move);splitter.addEventListener('pointerup',finish,{once:true});});
  splitter.addEventListener('keydown',event=>{if(event.key==='ArrowLeft'||event.key==='ArrowRight'){event.preventDefault();state.sidebarWidth=Math.max(190,Math.min(380,state.sidebarWidth+(event.key==='ArrowRight'?10:-10)));applyTheme();save();}});
  body.append(sidebar,splitter,content);shell.append(topbar,body,statusbar,parking);app.replaceChildren(shell);
  new ResizeObserver(()=>requestAnimationFrame(()=>terminals.fitAll())).observe(content);
}

function render(){
  if(monitorMode){renderMonitor();return;}
  const hadTerminalFocus=!!document.activeElement?.closest('.terminal')&&!document.querySelector('dialog[open]');
  renderSidebar();renderWorkspace();renderStatus();
  if(hadTerminalFocus)requestAnimationFrame(()=>{
    // Reparenting xterm's retained DOM blurs its textarea. Restore only if an
    // intervening click or dialog has not intentionally taken focus elsewhere.
    if(document.querySelector('dialog[open]'))return;
    const focused=document.activeElement;
    if(focused!==document.body&&focused!==document.documentElement&&!focused?.closest('.terminal'))return;
    const id=activePane()?.activeTabId;if(id)terminals.focus(id);
  });
}
function renderSidebar(){
  for(const node of sidebarFooter.querySelectorAll<HTMLElement>('[data-sidebar-mode]'))node.classList.toggle('active',node.dataset.sidebarMode===(fileMode?'files':'projects'));
  fileTree.retain(new Set(state.workspaces.map(workspace=>workspace.id)));
  sidebarContent.classList.toggle('file-mode',fileMode);
  if(fileMode){
    const workspace=activeWorkspace();
    if(workspace)fileTree.show(workspace,sidebarContent);
    else{fileTree.hide();sidebarContent.replaceChildren(el('div','sidebar-empty','打开项目后查看文件。'));}
    return;
  }
  fileTree.hide();sidebarContent.replaceChildren();
  const sorted=[...state.workspaces].sort((a,b)=>Number(b.pinned)-Number(a.pinned));
  if(!sorted.length){const empty=el('div','sidebar-empty');empty.append(el('p','','打开一个文件夹，开始工作。'),labeledButton('打开项目',()=>run(()=>openWorkspace()),'folder','secondary'));sidebarContent.append(empty);}
  for(const workspace of sorted){
    const group=el('section','workspace-group');group.dataset.workspaceId=workspace.id;group.classList.toggle('active',workspace.id===state.activeWorkspaceId);
    const row=el('div','workspace-row');const select=button(workspace.path,()=>selectWorkspace(workspace.id));select.className='workspace-select';select.replaceChildren();select.append(icon('folder',15),el('span','workspace-name',workspace.name));if(workspace.pinned)select.append(icon('pin',12));
    const dots=el('span','workspace-dots');for(const sessionId of panes(workspace.tree).flatMap(p=>p.tabs)){const session=sessions.get(sessionId);if(session?.running&&liveIds.has(session.id)){const dot=el('i',`status-dot ${activity(session).kind}`);dot.title=`${session.title} · ${activity(session).label}`;dots.append(dot);}}select.append(dots);
    const options=button(`${workspace.name} 操作`,()=>menu(workspaceMenu(workspace),options),'more','icon-button workspace-options');row.append(select,options);row.addEventListener('contextmenu',event=>{event.preventDefault();menu(workspaceMenu(workspace),{x:event.clientX,y:event.clientY});});
    row.addEventListener('dragover',event=>{if(hasTabDrag(event)){event.preventDefault();row.classList.add('drop-target');}});row.addEventListener('dragleave',()=>row.classList.remove('drop-target'));row.addEventListener('drop',event=>{row.classList.remove('drop-target');dropTab(event,workspace,panes(workspace.tree)[0]);});
    group.append(row);
    const tabList=el('div','vertical-tabs');for(const pane of panes(workspace.tree))for(const sessionId of pane.tabs){const session=sessions.get(sessionId);if(!session)continue;const item=el('div','vertical-tab');const selected=workspace.id===state.activeWorkspaceId&&pane.activeTabId===sessionId&&workspace.activePaneId===pane.id;item.classList.toggle('selected',selected);item.dataset.sessionId=sessionId;
      const selectTab=button(session.title,()=>selectSession(workspace,pane,sessionId));selectTab.className='vertical-tab-select';selectTab.replaceChildren();selectTab.append(agentIcon(session.agentId),el('span','tab-label',session.title),el('i',`status-dot ${activity(session).kind}`));selectTab.title=`${session.title}\n${activity(session).label}\n${session.cwd}`;
      item.append(selectTab,button('关闭终端',()=>run(()=>closeSession(sessionId)),'close','icon-button tab-close'));wireTab(item,sessionId,workspace,pane);tabList.append(item);
    }
    const add=labeledButton('新建终端',()=>showLaunchMenu(add,workspace,findPane(workspace.tree,workspace.activePaneId)||panes(workspace.tree)[0]),'plus','new-session-inline');tabList.append(add);group.append(tabList);sidebarContent.append(group);
  }
  const addProject=labeledButton('打开项目',()=>run(()=>openWorkspace()),'plus','add-project');sidebarContent.append(addProject);
}
function workspaceMenu(workspace:Workspace):MenuItem[]{return [
  {label:workspace.pinned?'取消固定':'固定项目',icon:'pin',action:()=>{workspace.pinned=!workspace.pinned;renderSidebar();save();}},
  {label:'重命名项目',icon:'file',action:()=>run(async()=>{const name=await prompt('重命名项目','项目名称',workspace.name);if(name){workspace.name=name;render();save();}})},
  {label:'在资源管理器中打开',icon:'folder',action:()=>run(()=>call('path.open',{path:workspace.path}))},
  {label:'复制项目路径',icon:'copy',action:()=>run(()=>call('clipboard.write',{text:workspace.path}))},
  {label:'关闭项目',icon:'close',danger:true,action:()=>run(async()=>{const ids=panes(workspace.tree).flatMap(p=>p.tabs);const running=ids.filter(id=>sessions.get(id)?.running&&liveIds.has(id)).length;if(!await confirm('关闭项目',running?`将关闭 ${workspace.name} 的 ${running} 个运行中终端及其子进程。项目会保留在最近打开中。`:`关闭 ${workspace.name}？项目会保留在最近打开中。`,'关闭项目'))return;for(const id of ids){if(liveIds.has(id))await call('session.close',{sessionId:id});terminals.dispose(id);sessions.delete(id);liveIds.delete(id);}state.workspaces=state.workspaces.filter(w=>w.id!==workspace.id);if(state.activeWorkspaceId===workspace.id)state.activeWorkspaceId=state.workspaces[0]?.id||null;render();save();})}
];}
function selectWorkspace(id:string){state.activeWorkspaceId=id;zoomPaneId=null;render();save();void inspectWorkspace().catch(error);focusTerminal();}
function selectSession(workspace:Workspace,pane:Pane,id:string){state.activeWorkspaceId=workspace.id;workspace.activePaneId=pane.id;pane.activeTabId=id;zoomPaneId=null;render();save();focusTerminal();}
function focusTerminal(){const id=activePane()?.activeTabId;if(id)requestAnimationFrame(()=>terminals.focus(id));}

function renderWorkspace(){
  terminals.parkAll();content.replaceChildren();const workspace=activeWorkspace();
  if(!workspace){const empty=el('div','welcome');const mark=logoImage('welcome-logo');empty.append(mark,el('h1','','把终端留给真正的工作。'),el('p','','打开项目，并行运行你的 Agent。'),labeledButton('打开项目',()=>run(()=>openWorkspace()),'folder','primary'),el('span','shortcut-hint','Ctrl Shift O  打开项目     ·     Ctrl P  快速切换'));if(state.recent.length){const list=el('div','welcome-recent');list.append(el('span','section-label','最近打开'));for(const recent of state.recent.slice(0,4))list.append(labeledButton(recent.name,()=>run(()=>openWorkspace(recent.path)),'folder','recent-welcome-item'));empty.append(list);}content.append(empty);return;}
  content.append(renderNode(workspace.tree,workspace));requestAnimationFrame(()=>{terminals.fitAll();renderFocus();});
}
function renderNode(node:PaneNode,workspace:Workspace):HTMLElement {
  if(node.type==='pane')return renderPane(node,workspace);
  if(zoomPaneId){const target=findPane(node,zoomPaneId);if(target)return renderPane(target,workspace);}
  const split=el('div',`split ${node.orientation}`);split.dataset.nodeId=node.id;const first=el('div','split-child'),second=el('div','split-child');const divider=el('div','split-divider');divider.tabIndex=0;divider.setAttribute('role','separator');divider.setAttribute('aria-orientation',node.orientation==='horizontal'?'vertical':'horizontal');divider.setAttribute('aria-label','调整分屏比例');
  const setFraction=(value:number)=>{node.fraction=Math.max(.15,Math.min(.85,value));first.style.flex=`${node.fraction} 1 0`;second.style.flex=`${1-node.fraction} 1 0`;divider.setAttribute('aria-valuenow',String(Math.round(node.fraction*100)));};setFraction(node.fraction);
  first.append(renderNode(node.first,workspace));second.append(renderNode(node.second,workspace));split.append(first,divider,second);
  divider.addEventListener('pointerdown',event=>{event.preventDefault();divider.setPointerCapture(event.pointerId);split.classList.add('resizing');const rect=split.getBoundingClientRect();const move=(e:PointerEvent)=>{setFraction(node.orientation==='horizontal'?(e.clientX-rect.left)/rect.width:(e.clientY-rect.top)/rect.height);terminals.fitAll();};const end=()=>{divider.removeEventListener('pointermove',move);split.classList.remove('resizing');save();};divider.addEventListener('pointermove',move);divider.addEventListener('pointerup',end,{once:true});divider.addEventListener('pointercancel',end,{once:true});});
  divider.addEventListener('keydown',event=>{if(['ArrowLeft','ArrowUp','ArrowRight','ArrowDown'].includes(event.key)){event.preventDefault();setFraction(node.fraction+(['ArrowRight','ArrowDown'].includes(event.key)?.03:-.03));terminals.fitAll();save();}});
  divider.addEventListener('dblclick',()=>{setFraction(.5);terminals.fitAll();save();});return split;
}
function renderPane(pane:Pane,workspace:Workspace):HTMLElement {
  const node=el('section','pane');node.dataset.paneId=pane.id;node.setAttribute('aria-label','终端分屏');
  node.addEventListener('dragover',event=>{if(hasTabDrag(event)){event.preventDefault();node.classList.add('tab-drop-target');}});
  node.addEventListener('dragleave',event=>{if(!event.relatedTarget||!node.contains(event.relatedTarget as Node))node.classList.remove('tab-drop-target');});
  node.addEventListener('drop',()=>node.classList.remove('tab-drop-target'),{capture:true});
  node.addEventListener('drop',event=>{if(hasTabDrag(event))dropTab(event,workspace,pane);});
  node.addEventListener('pointerdown',()=>{if(workspace.activePaneId!==pane.id){workspace.activePaneId=pane.id;renderFocus();renderSidebar();renderStatus();save();}});
  const tabbar=el('div','pane-tabbar'),tabs=el('div','pane-tabs');tabs.setAttribute('role','tablist');
  for(const id of pane.tabs){const session=sessions.get(id);if(!session)continue;const tab=el('div','pane-tab');tab.classList.toggle('active',id===pane.activeTabId);tab.dataset.sessionId=id;tab.setAttribute('role','presentation');
    const select=button(session.title,()=>selectSession(workspace,pane,id));select.className='pane-tab-select';select.replaceChildren();select.setAttribute('role','tab');select.setAttribute('aria-selected',String(id===pane.activeTabId));select.append(agentIcon(session.agentId),el('span','tab-label',session.title),el('i',`status-dot ${activity(session).kind}`));tab.append(select,button('关闭终端',()=>run(()=>closeSession(id)),'close','icon-button tab-close'));wireTab(tab,id,workspace,pane);tabs.append(tab);
  }
  const add=button('新建终端',()=>showLaunchMenu(add,workspace,pane),'plus','icon-button');tabs.append(add);
  tabbar.addEventListener('dragover',event=>{if(hasTabDrag(event)){event.preventDefault();tabbar.classList.add('drop-target');}});tabbar.addEventListener('dragleave',()=>tabbar.classList.remove('drop-target'));tabbar.addEventListener('drop',event=>{tabbar.classList.remove('drop-target');dropTab(event,workspace,pane);});
  const actions=el('div','pane-actions');actions.append(button('向右分屏 · Ctrl Shift D',()=>run(()=>split(workspace,pane,'horizontal')),'splitRight','icon-button'),button('向下分屏 · Ctrl Alt D',()=>run(()=>split(workspace,pane,'vertical')),'splitDown','icon-button'),button('最大化分屏 · Ctrl Shift E',()=>{workspace.activePaneId=pane.id;zoomPaneId=zoomPaneId?null:pane.id;renderWorkspace();focusTerminal();},'expand','icon-button'));if(!pane.tabs.length&&panes(workspace.tree).length>1)actions.append(button('关闭空分屏',()=>{workspace.tree=removePane(workspace.tree,pane.id)||newPane();workspace.activePaneId=panes(workspace.tree)[0].id;render();save();},'close','icon-button'));
  tabbar.append(tabs,actions);node.append(tabbar);
  const terminalArea=el('div','terminal-area');const session=pane.activeTabId?sessions.get(pane.activeTabId):undefined;
  if(session){
    terminals.attach(session,terminalArea,liveIds.has(session.id));
    if(!session.running||!liveIds.has(session.id)){
      const ended=el('div','session-ended');
      // The transparent WebGL link canvas has z-index 2 and otherwise receives
      // clicks through this visible overlay. Keep native-session controls above it.
      ended.style.zIndex='12';
      const resume=labeledButton(resumingSessions.has(session.id)?'正在恢复…':session.nativeSessionId?'恢复会话':'重新启动',()=>{
        resume.disabled=true;
        void resumeSession(session,workspace,pane).catch(error).finally(()=>{resume.disabled=false;});
      },'refresh','small-button');
      resume.dataset.resumeSession=session.id;
      resume.disabled=resumingSessions.has(session.id);
      ended.append(el('span','',liveIds.has(session.id)?`进程已退出${session.exitCode==null?'':`，退出代码 ${session.exitCode}`}。`:session.nativeSessionId?'此会话可通过原生 Session ID 恢复。':'会话进程未运行。重新启动将创建新的终端。'),resume);
      terminalArea.append(ended);
    }
  }
  else {const empty=el('div','pane-empty');empty.append(icon('terminal',28),el('p','','启动一个终端'),el('span','muted','在这个项目中运行 Agent 或 Shell'));const choices=el('div','empty-launchers');for(const agent of agents.filter(a=>['claude','codex','shell'].includes(a.id))){const b=labeledButton(agent.name,()=>run(()=>launch(agent.id,workspace,pane)),undefined,'launcher');b.prepend(agentIcon(agent.id));if(!agent.available){b.title=`未在 PATH 中发现 ${agent.command}，点击可尝试启动`;b.classList.add('unavailable');}choices.append(b);}empty.append(choices);terminalArea.append(empty);}
  node.append(terminalArea);const footer=el('div','pane-status');footer.dataset.statusFor=pane.id;node.append(footer);renderPaneStatus(footer,pane,workspace);return node;
}
function renderFocus(){for(const pane of content.querySelectorAll<HTMLElement>('.pane'))pane.classList.toggle('focused',pane.dataset.paneId===activeWorkspace()?.activePaneId);}
function renderPaneStatus(footer:HTMLElement,pane:Pane,workspace:Workspace){footer.replaceChildren();const session=pane.activeTabId?sessions.get(pane.activeTabId):undefined;const status=session?activity(session):null;
  if(session&&status){const left=el('span','pane-activity');left.append(el('i',`status-dot ${status.kind}`),el('span','activity-label',status.label));left.title=`仅根据进程存活与终端输出显示状态，不推断权限审批。${session.nativeSessionId?`\nSession: ${session.nativeSessionId}`:''}`;footer.append(left);const path=el('span','pane-cwd',compactPath(session.cwd));path.title=session.cwd;footer.append(path);}
  else footer.append(el('span','pane-cwd',compactPath(workspace.path)));
  const cwd=panePath(pane,workspace),info=workspaceInfo.get(workspaceKey(cwd));if(info?.git.isRepository){const branch=el('span','branch-pill');branch.append(icon('branch',12),el('span','',`${info.git.branch||'detached'}${info.git.error?' · 部分':''}`));branch.title=`${cwd}${info.git.error?`\n${info.git.error}`:''}`;footer.append(branch);}
}
function renderStatus(){
  statusbar.replaceChildren();const workspace=activeWorkspace();const current=activeSession();const left=el('div','status-left');left.append(el('i',`status-dot ${connectionActive?'running':'disconnected'}`),el('span','',connectionActive?'本地终端':'终端服务未连接'));
  const cwd=workspace?(current?.cwd||workspace.path):null,key=cwd?workspaceKey(cwd):null,info=key?workspaceInfo.get(key):undefined,probeError=key?workspaceInspectionErrors.get(key):undefined;
  if(workspace){workspaceTitle.textContent=workspace.name;left.append(el('span','status-divider','/'),el('span','status-workspace',workspace.name));if(info?.git.isRepository){const git=el('span','status-git');git.append(icon('branch',13),el('span','',info.git.branch||'detached'),el('span','muted',`${info.git.changedFiles} 个变更${info.git.error ? ' · 部分' : ''}`),el('span','added',`+${info.git.added}`),el('span','deleted',`−${info.git.deleted}`));git.title=`${cwd}\n${info.git.error||'当前目录的仓库变更（含暂存区与工作树）'}`;left.append(git);}if((info?.git.error&&!info.git.isRepository)||probeError){const warning=el('span','muted','Git 不可用');warning.title=probeError||info?.git.error||'';left.append(warning);}}
  const right=el('div','status-right');
  if(workspace){
    const scope='基于 Weirdo 启动环境在当前目录进行探测，不跟踪终端内临时修改的 PATH 或激活环境。';
    const node=el('span','runtime-label',info?.node?`Node ${info.node.replace(/^v/,'')}`:info||probeError?'Node —':'Node …');
    node.title=`${probeError?`检测失败：${probeError}`:info?info.node?'':'未在工作区启动环境中检测到。':'等待当前目录的环境检测。'}\n${scope}`;
    const python=el('span','runtime-label',info?.python?`Python ${info.python.replace(/^Python\s*/i,'')}`:info||probeError?'Python —':'Python …');
    python.title=`${probeError?`检测失败：${probeError}`:info?info.python?'':'未在工作区启动环境中检测到。':'等待当前目录的环境检测。'}\n${scope}`;
    node.style.display='inline';python.style.display='inline';
    right.append(node,python);if(info?.environment){const environment=el('span','runtime-label',info.environment);environment.title=scope;right.append(environment);}
  }
  right.append(el('span','muted',`${[...sessions.values()].filter(s=>s.running&&liveIds.has(s.id)).length} 个运行中`));if(current?.running)right.append(el('span','muted',`PID ${current.processId}`));statusbar.append(left,right);
}

async function openWorkspace(path?:string){
  const location=path
    ?await call<WorkspaceLocation>('workspace.resolve',{path})
    :await chooseWorkspace({
      workspaces:()=>state.workspaces,
      recent:()=>state.recent,
      initialPath:activeWorkspace()?.path||state.recent[0]?.path
    });
  if(!location)return;
  const existing=state.workspaces.find(workspace=>workspaceKey(workspace.path)===workspaceKey(location.path));
  if(existing){selectWorkspace(existing.id);return;}
  const pane=newPane();
  const workspace:Workspace={id:crypto.randomUUID(),name:location.name,path:location.path,pinned:false,tree:pane,activePaneId:pane.id};
  state.workspaces.push(workspace);state.activeWorkspaceId=workspace.id;
  state.recent=[{path:location.path,name:location.name,lastOpened:new Date().toISOString()},...state.recent.filter(recent=>workspaceKey(recent.path)!==workspaceKey(location.path))].slice(0,30);
  zoomPaneId=null;render();save();
  void inspectWorkspace().catch(error);
}
function startupModelLabel(agent:AgentProfile) {
  return agent.model || agent.env.ANTHROPIC_MODEL || '跟随 CLI 配置';
}

function showLaunchMenu(anchor:HTMLElement,workspace=activeWorkspace(),pane=activePane()) {
  if(!workspace||!pane){run(()=>openWorkspace());return;}
  const launchItems:MenuItem[]=agents.map(agent=>({
    label:agent.name,
    icon:'terminal',
    detail:agent.kind==='claude'?startupModelLabel(agent):agent.available?undefined:'PATH 未发现',
    action:()=>run(()=>launch(agent.id,workspace,pane))
  }));
  const modelItems:MenuItem[]=agents.filter(agent=>agent.kind==='claude').map(agent=>({
    label:`${agent.name} 默认模型…`,
    icon:'settings',
    action:()=>editAgent(agent,undefined,true)
  }));
  menu([...launchItems,...modelItems,{label:'恢复原生会话…',icon:'history',action:()=>openHistory(workspace,pane)},{label:'管理 Agent…',icon:'settings',action:openSettings}],anchor);
}
type SessionDestination=Pick<Workspace,'id'|'path'|'name'|'pinned'>&{paneId:string};
function captureDestination(workspace:Workspace,pane:Pane):SessionDestination{return {id:workspace.id,path:workspace.path,name:workspace.name,pinned:workspace.pinned,paneId:pane.id};}
function resolveDestination(target:SessionDestination,sessionId:string,previousId?:string):{workspace:Workspace;pane:Pane}{
  // A peer may have already attached the result, or moved the old tab while the
  // native RPC was pending. Find that current owner before using captured IDs.
  for(const id of [sessionId,previousId])if(id)for(const workspace of state.workspaces){const pane=panes(workspace.tree).find(p=>p.tabs.includes(id));if(pane)return {workspace,pane};}
  let workspace=state.workspaces.find(w=>w.id===target.id)||state.workspaces.find(w=>w.path.toLowerCase()===target.path.toLowerCase());
  if(!workspace){
    // Restore only the destination, not its stale tabs, so an accepted launch
    // remains reachable if another window closed the project during startup.
    const pane=newPane();workspace={id:target.id,path:target.path,name:target.name,pinned:target.pinned,tree:pane,activePaneId:pane.id};state.workspaces.push(workspace);
  }
  return {workspace,pane:findPane(workspace.tree,target.paneId)||panes(workspace.tree)[0]};
}
function attachStartedSession(session:SessionInfo,target:SessionDestination,previousId?:string){
  const {workspace,pane}=resolveDestination(target,session.id,previousId);
  const replaceId=previousId&&pane.tabs.includes(previousId)?previousId:session.id;
  const index=pane.tabs.indexOf(replaceId);
  const beforeId=index>=0?pane.tabs.slice(index+1).find(id=>id!==session.id):undefined;
  if(previousId&&previousId!==session.id){
    for(const currentWorkspace of state.workspaces)for(const currentPane of panes(currentWorkspace.tree)){
      const at=currentPane.tabs.indexOf(previousId);if(at<0)continue;
      currentPane.tabs.splice(at,1);if(currentPane.activeTabId===previousId)currentPane.activeTabId=currentPane.tabs[Math.max(0,at-1)]||null;
    }
    terminals.dispose(previousId);sessions.delete(previousId);liveIds.delete(previousId);
  }
  mergeSession(session);liveIds.add(session.id);moveTab(state.workspaces,session.id,pane,beforeId);
  workspace.activePaneId=pane.id;state.activeWorkspaceId=workspace.id;
  if(session.workspaceId!==workspace.id)void call('session.move',{sessionId:session.id,workspaceId:workspace.id}).catch(error);
  render();save();focusTerminal();
}
async function launch(agentId:string,workspace=activeWorkspace(),pane=activePane(),nativeSessionId?:string){
  if(!workspace||!pane)return;const target=captureDestination(workspace,pane);
  const session=await call<SessionInfo>('session.create',{workspaceId:target.id,cwd:target.path,agentId,nativeSessionId,cols:100,rows:30});
  attachStartedSession(session,target);
}
async function split(workspace:Workspace,pane:Pane,orientation:'horizontal'|'vertical'){
  const currentWorkspace=state.workspaces.find(candidate=>candidate.id===workspace.id);
  const currentPane=currentWorkspace&&findPane(currentWorkspace.tree,pane.id);
  if(!currentWorkspace||!currentPane){toast('目标分屏已关闭。');return;}
  if(panes(currentWorkspace.tree).length>=12){toast('每个项目最多显示 12 个分屏。');return;}
  zoomPaneId=null;
  state.activeWorkspaceId=currentWorkspace.id;
  splitPane(currentWorkspace,currentPane.id,orientation);
  render();save();
}
async function closeSession(id:string){const session=sessions.get(id);if(!session)return;if(session.running&&liveIds.has(id)&&!await confirm('关闭终端',`关闭「${session.title}」将停止此终端和它启动的子进程。`,'关闭终端'))return;if(liveIds.has(id))await call('session.close',{sessionId:id});for(const workspace of state.workspaces)for(const pane of panes(workspace.tree)){const index=pane.tabs.indexOf(id);if(index>=0){pane.tabs.splice(index,1);if(pane.activeTabId===id)pane.activeTabId=pane.tabs[Math.max(0,index-1)]||null;}}liveIds.delete(id);sessions.delete(id);terminals.dispose(id);render();save();focusTerminal();}
async function resumeSession(session:SessionInfo,workspace:Workspace,pane:Pane){
  const previousId=session.id;if(resumingSessions.has(previousId))return;
  const target=captureDestination(workspace,pane);resumingSessions.add(previousId);
  try{
    const next=liveIds.has(previousId)
      ?await call<SessionInfo>('session.resume',{sessionId:previousId})
      :await call<SessionInfo>('session.create',{workspaceId:target.id,cwd:session.cwd||target.path,agentId:session.agentId,title:session.title,nativeSessionId:session.nativeSessionId,cols:session.cols,rows:session.rows});
    attachStartedSession(next,target,previousId);
  }finally{
    resumingSessions.delete(previousId);
    for(const button of document.querySelectorAll<HTMLButtonElement>('[data-resume-session]'))if(button.dataset.resumeSession===previousId){
      button.disabled=false;const label=button.querySelector('span');if(label)label.textContent=sessions.get(previousId)?.nativeSessionId?'恢复会话':'重新启动';
    }
  }
}
function hasTabDrag(event:DragEvent){return !!event.dataTransfer?.types.includes('application/x-weirdo-session');}
function wireTab(node:HTMLElement,id:string,workspace:Workspace,pane:Pane){node.draggable=true;node.addEventListener('dragstart',event=>{event.dataTransfer!.setData('application/x-weirdo-session',JSON.stringify({id,session:sessions.get(id)}));event.dataTransfer!.effectAllowed='move';node.classList.add('dragging');});node.addEventListener('dragend',()=>node.classList.remove('dragging'));node.addEventListener('dragover',event=>{if(hasTabDrag(event)){event.preventDefault();event.stopPropagation();node.classList.add('drop-target');}});node.addEventListener('dragleave',()=>node.classList.remove('drop-target'));node.addEventListener('drop',event=>{node.classList.remove('drop-target');dropTab(event,workspace,pane,id);});node.addEventListener('dblclick',()=>run(()=>renameSession(id)));node.addEventListener('contextmenu',event=>{event.preventDefault();menu([{label:'重命名',icon:'file',action:()=>run(()=>renameSession(id))},{label:'复制 Session ID',icon:'copy',disabled:!sessions.get(id)?.nativeSessionId,action:()=>run(()=>call('clipboard.write',{text:sessions.get(id)?.nativeSessionId||''}))},{label:'复制终端文本',icon:'copy',action:()=>run(()=>call('clipboard.write',{text:terminals.text(id)}))},{label:'关闭终端',icon:'close',danger:true,action:()=>run(()=>closeSession(id))}],{x:event.clientX,y:event.clientY});});}
function dropTab(event:DragEvent,workspace:Workspace,pane:Pane,beforeId?:string){if(!hasTabDrag(event))return;event.preventDefault();event.stopPropagation();try{const data=JSON.parse(event.dataTransfer!.getData('application/x-weirdo-session'));if(typeof data.id!=='string'||data.id===beforeId)return;if(!sessions.has(data.id)&&data.session?.id===data.id){mergeSession(data.session);liveIds.add(data.id);}if(!sessions.has(data.id))return;moveTab(state.workspaces,data.id,pane,beforeId);workspace.activePaneId=pane.id;state.activeWorkspaceId=workspace.id;void call('session.move',{sessionId:data.id,workspaceId:workspace.id}).catch(error);render();save();focusTerminal();}catch(errorValue){error(errorValue);}}
async function renameSession(id:string){const session=sessions.get(id);if(!session)return;const name=await prompt('重命名终端','终端名称',session.title);if(!name)return;if(liveIds.has(id))await call('session.rename',{sessionId:id,title:name});session.title=name;render();save();}

function inspectPath(path:string):Promise<WorkspaceInfo>{
  const key=workspaceKey(path),pending=workspaceInspections.get(key);if(pending)return pending;
  const request=call<WorkspaceInfo>('workspace.inspect',{path}).then(info=>{
    workspaceInfo.set(key,info);workspaceInspectionErrors.delete(key);
    if(workspaceInfo.size>128)workspaceInfo.delete(workspaceInfo.keys().next().value!);
    return info;
  }).catch(cause=>{
    workspaceInfo.delete(key);workspaceInspectionErrors.set(key,cause instanceof Error?cause.message:String(cause));
    if(workspaceInspectionErrors.size>128)workspaceInspectionErrors.delete(workspaceInspectionErrors.keys().next().value!);
    throw cause;
  }).finally(()=>workspaceInspections.delete(key));
  workspaceInspections.set(key,request);return request;
}
async function inspectWorkspace(){
  const workspace=activeWorkspace();if(!workspace)return;const token=++inspectionId;
  const paths=new Map<string,string>();const activePath=activeSession()?.cwd||workspace.path;paths.set(workspaceKey(activePath),activePath);
  for(const node of content.querySelectorAll<HTMLElement>('.pane[data-pane-id]')){
    const pane=findPane(workspace.tree,node.dataset.paneId!);if(!pane)continue;
    const path=panePath(pane,workspace);paths.set(workspaceKey(path),path);if(paths.size>=12)break;
  }
  const results=await Promise.allSettled([...paths.values()].map(inspectPath));
  if(token===inspectionId){
    renderStatus();const current=activeWorkspace();
    if(current)for(const footer of content.querySelectorAll<HTMLElement>('[data-status-for]')){const pane=findPane(current.tree,footer.dataset.statusFor!);if(pane)renderPaneStatus(footer,pane,current);}
    const failure=results.find((result):result is PromiseRejectedResult=>result.status==='rejected');if(failure)throw failure.reason;
  }
}
function quotePath(path:string){return `'${path.replace(/'/g,"''")}'`;}
function insertPath(path:string){const session=activeSession();if(!session?.running){toast('先选择一个运行中的终端。');return;}terminals.insert(session.id,`${quotePath(path)} `);}
content.addEventListener('dragover',event=>{if(event.dataTransfer?.types.includes('application/x-weirdo-path'))event.preventDefault();});content.addEventListener('drop',event=>{const path=event.dataTransfer?.getData('application/x-weirdo-path');if(path){event.preventDefault();const paneNode=(event.target as HTMLElement).closest<HTMLElement>('[data-pane-id]');const workspace=activeWorkspace();if(workspace&&paneNode)workspace.activePaneId=paneNode.dataset.paneId!;insertPath(path);}});
document.addEventListener('dragend',()=>{for(const node of document.querySelectorAll('.pane.tab-drop-target'))node.classList.remove('tab-drop-target');});

function openRecent(){run(()=>openWorkspace());}
function openQuickOpen(){
  const d=dialog('快速切换',true);d.element.classList.add('quick-dialog');const search=el('input','command-search');search.placeholder='搜索项目、终端或 Agent…';search.setAttribute('aria-label','搜索项目、终端或 Agent');const list=el('div','command-results');d.body.append(search,list);let selected=0;let entries:{title:string;detail:string;icon:string;action:()=>void}[]=[];
  const renderResults=()=>{const query=search.value.toLocaleLowerCase().trim();const all:{title:string;detail:string;icon:string;action:()=>void}[]=[];
    for(const workspace of state.workspaces){all.push({title:workspace.name,detail:workspace.path,icon:'folder',action:()=>selectWorkspace(workspace.id)});for(const pane of panes(workspace.tree))for(const id of pane.tabs){const session=sessions.get(id);if(session)all.push({title:session.title,detail:`${workspace.name} · ${activity(session).label}`,icon:'terminal',action:()=>selectSession(workspace,pane,id)});}}
    for(const agent of agents)all.push({title:`启动 ${agent.name}`,detail:activeWorkspace()?.name||'先打开项目',icon:'plus',action:()=>run(async()=>{if(!activeWorkspace())await openWorkspace();await launch(agent.id);})});
    for(const recent of state.recent.filter(r=>!state.workspaces.some(w=>w.path.toLowerCase()===r.path.toLowerCase())))all.push({title:recent.name,detail:`最近 · ${recent.path}`,icon:'history',action:()=>run(()=>openWorkspace(recent.path))});
    all.push({title:'打开项目',detail:'选择文件夹',icon:'folder',action:()=>run(()=>openWorkspace())},{title:'Agent 与外观设置',detail:'命令、环境变量、主题',icon:'settings',action:openSettings},{title:'Agent 活动监视器',detail:'独立窗口',icon:'monitor',action:()=>run(()=>call('window.monitor'))});
    entries=all.filter(item=>!query||`${item.title} ${item.detail}`.toLocaleLowerCase().includes(query)).slice(0,40);selected=Math.max(0,Math.min(selected,entries.length-1));list.replaceChildren();entries.forEach((item,index)=>{const row=labeledButton(item.title,()=>{d.close();item.action();},item.icon,'command-result');row.classList.toggle('selected',index===selected);row.append(el('span','command-detail',item.detail));list.append(row);});if(!entries.length)list.append(el('p','empty-copy','未找到匹配项。'));
  };
  search.addEventListener('input',()=>{selected=0;renderResults();});search.addEventListener('keydown',event=>{if(event.key==='ArrowDown'||event.key==='ArrowUp'){event.preventDefault();selected=Math.max(0,Math.min(entries.length-1,selected+(event.key==='ArrowDown'?1:-1)));renderResults();list.children[selected]?.scrollIntoView({block:'nearest'});}if(event.key==='Enter'&&entries[selected]){const entry=entries[selected];d.close();entry.action();}});renderResults();search.focus();
}

function openHistory(workspace:Workspace,pane:Pane){const d=dialog('恢复原生会话',true);const filters=el('div','history-filters');const select=el('select');select.setAttribute('aria-label','Agent');for(const agent of agents.filter(a=>['claude','codex','gemini'].includes(a.kind)||['claude','codex','gemini'].includes(a.id))){const option=el('option','',agent.name);option.value=agent.id;select.append(option);}filters.append(select,el('span','muted truncate',workspace.path));const list=el('div','session-history');d.body.append(filters,list);const load=async()=>{list.replaceChildren(el('p','muted','正在读取原生会话记录…'));const history=await call<NativeSession[]>('agents.history',{agentId:select.value,cwd:workspace.path});if(!d.element.open)return;list.replaceChildren();if(!history.length)list.append(el('p','empty-copy','此项目暂无可恢复的原生会话。'));for(const session of history){const row=el('button','history-session');row.type='button';row.append(el('strong','truncate',session.title||session.id),el('span','muted',new Date(session.modifiedAt).toLocaleString()),el('code','truncate',session.id));row.addEventListener('click',()=>{d.close();run(()=>launch(select.value,workspace,pane,session.id));});list.append(row);}};select.addEventListener('change',()=>run(load));run(load);const manual=input('或输入原生 Session ID');manual.input.placeholder='会话 UUID';d.body.append(manual.label);d.footer.append(labeledButton('取消',d.close),labeledButton('恢复指定会话',()=>{const id=manual.input.value.trim();if(!id){manual.input.focus();return;}d.close();run(()=>launch(select.value,workspace,pane,id));},'history','primary'));}

function openSettings(){
  const d=dialog('设置',true);const tabs=el('div','settings-tabs');const panel=el('div','settings-panel');d.body.append(tabs,panel);let section='agents';const renderSection=()=>{for(const tab of tabs.querySelectorAll<HTMLElement>('button'))tab.classList.toggle('active',tab.dataset.section===section);panel.replaceChildren();if(section==='agents')renderAgentSettings(panel);if(section==='appearance')renderAppearanceSettings(panel);if(section==='about')renderAboutSettings(panel);};for(const [id,label]of [['agents','Agent'],['appearance','外观与快捷键'],['about','关于与更新']]){const tab=labeledButton(label,()=>{section=id;renderSection();});tab.dataset.section=id;tabs.append(tab);}renderSection();d.footer.append(labeledButton('完成',d.close,undefined,'primary'));
}
function renderAgentSettings(panel:HTMLElement){
  panel.append(el('p','settings-description','直接运行官方 CLI，沿用你的登录、模型、权限和代理配置。自定义环境变量仅对这个 Agent 的子进程生效。'));
  const list=el('div','agent-settings-list');
  for(const agent of agents){
    const row=el('div','agent-settings-row');
    const label=el('div','agent-settings-label');
    label.append(agentIcon(agent.id),el('strong','',agent.name),el('span',`availability ${agent.available?'available':''}`,agent.available?'已发现':'PATH 未发现'));
    const details=el('div','agent-settings-details');
    details.append(el('code','agent-command',agent.command));
    if(agent.kind==='claude')details.append(el('span','agent-model-summary',`启动模型 ${startupModelLabel(agent)}`));
    const actions=el('div','agent-settings-actions');
    if(agent.kind==='claude')actions.append(labeledButton('模型',()=>editAgent(agent,()=>renderAgentSettingsInto(panel),true)));
    actions.append(labeledButton('编辑',()=>editAgent(agent,()=>renderAgentSettingsInto(panel))));
    if(!agent.builtIn)actions.append(button('删除 Agent',()=>run(async()=>{
      if(await confirm('删除 Agent',`删除「${agent.name}」配置？正在运行的终端继续保留。`,'删除')){
        agents=await call<AgentProfile[]>('agents.save',{agents:agents.filter(a=>a.id!==agent.id)});
        renderAgentSettingsInto(panel);
      }
    }),'close','icon-button'));
    row.append(label,details,actions);list.append(row);
  }
  panel.append(list,labeledButton('添加自定义 Agent',()=>editAgent(undefined,()=>renderAgentSettingsInto(panel)),'plus','secondary'));
}
function renderAgentSettingsInto(panel:HTMLElement){panel.replaceChildren();renderAgentSettings(panel);}
function editAgent(existing?:AgentProfile,done?:()=>void,focusModel=false){
  const d=dialog(existing?`编辑 ${existing.name}`:'添加自定义 Agent',true);
  const name=input('名称',existing?.name||'');
  const command=input('命令',existing?.command||'');
  command.input.placeholder='例如 claude';
  const grid=el('div','form-grid');grid.append(name.label,command.label);d.body.append(grid);

  const kindLabel=el('label','field');
  kindLabel.append(el('span','field-label','原生会话类型'));
  const kind=el('select');
  for(const [id,label]of [['custom','自定义命令'],['claude','Claude Code'],['codex','Codex CLI'],['gemini','Gemini CLI'],['shell','Shell']]){
    const option=el('option','',label);option.value=id;kind.append(option);
  }
  kind.value=existing?.kind||'custom';kindLabel.append(kind);d.body.append(kindLabel);

  const envLabel=el('label','field');
  envLabel.append(el('span','field-label','环境变量 · 每行 KEY=VALUE'));
  const env=el('textarea','env-editor');
  env.spellcheck=false;env.rows=6;
  env.value=Object.entries(existing?.env||{}).map(([key,value])=>`${key}=${value}`).join('\n');
  env.placeholder='ANTHROPIC_BASE_URL=https://…';envLabel.append(env);

  const modelSection=el('section','claude-model-settings');
  const model=input('新会话默认模型',existing?.model||'');
  model.input.placeholder='留空跟随 CLI 配置或环境变量';
  model.input.autocomplete='off';model.input.spellcheck=false;
  const suggestions=el('datalist');suggestions.id=`model-options-${crypto.randomUUID()}`;
  model.input.setAttribute('list',suggestions.id);
  const inherited=el('p','model-inherited');
  const modelHint=el('p','settings-note','可选择别名，也可输入完整模型 ID。此设置用于启动新会话。当前会话输入 /model 打开 Claude 原生模型选择器。');
  modelHint.id=`model-hint-${crypto.randomUUID()}`;
  model.input.setAttribute('aria-describedby',modelHint.id);

  let configuredModels:string[]=[];
  let inheritedModel:string|null=null;
  const environmentValue=(key:string)=>{
    for(const line of env.value.split(/\r?\n/).reverse()){
      const index=line.indexOf('=');
      if(index>0&&line.slice(0,index).trim().toUpperCase()===key)return line.slice(index+1);
    }
    return undefined;
  };
  const refreshModelSuggestions=()=>{
    const values=new Set(['default','sonnet','opus','haiku',...configuredModels]);
    for(const key of ['ANTHROPIC_MODEL','ANTHROPIC_DEFAULT_SONNET_MODEL','ANTHROPIC_DEFAULT_OPUS_MODEL','ANTHROPIC_DEFAULT_HAIKU_MODEL']){
      const value=environmentValue(key);if(value?.trim())values.add(value.trim());
    }
    if(existing?.model)values.add(existing.model);
    if(inheritedModel)values.add(inheritedModel);
    suggestions.replaceChildren();
    for(const value of values){const option=el('option');option.value=value;suggestions.append(option);}
    const current=environmentValue('ANTHROPIC_MODEL')||inheritedModel;
    inherited.textContent=current?`当前继承值 ${current}`:'留空时由 Claude CLI 选择默认模型';
  };

  const discoveryVariable='CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY';
  const discoveryRow=el('div','gateway-discovery');
  const discoveryLabel=el('label','checkbox-field');
  const discovery=el('input');discovery.type='checkbox';
  discoveryLabel.append(discovery,el('span','','启用 Claude 原生模型发现'));
  const discoveryState=el('span','model-inherited');
  const refreshDiscovery=()=>{
    const value=environmentValue(discoveryVariable);
    discovery.indeterminate=value===undefined;
    discovery.checked=value==='1'||value?.toLowerCase()==='true';
    discoveryState.textContent=value===undefined?'跟随启动环境':`新会话覆盖值 ${value}`;
  };
  const setDiscovery=(value:string|undefined)=>{
    const lines=env.value.split(/\r?\n/).filter(line=>{
      const index=line.indexOf('=');
      return index<0||line.slice(0,index).trim().toUpperCase()!==discoveryVariable;
    });
    if(value!==undefined)lines.push(`${discoveryVariable}=${value}`);
    env.value=lines.join('\n');refreshDiscovery();
  };
  discovery.addEventListener('change',()=>setDiscovery(discovery.checked?'1':'0'));
  discoveryRow.append(discoveryLabel,labeledButton('跟随环境',()=>setDiscovery(undefined),undefined,'small-button'));
  const gatewayResult=el('p','model-inherited');
  gatewayResult.setAttribute('aria-live','polite');
  const gatewayButton=labeledButton('读取网关模型',()=>{
    if(!existing||existing.kind!=='claude')return;
    gatewayButton.disabled=true;
    const label=gatewayButton.querySelector('span');if(label)label.textContent='读取中…';
    gatewayResult.classList.remove('error-text');
    gatewayResult.textContent='正在按已保存的 Agent 配置读取模型列表…';
    void call<{suggestions:string[];nativePickerUpdated:boolean;note?:string|null}>('agents.models.sync',{agentId:existing.id}).then(result=>{
      if(!d.element.open)return;
      configuredModels=[...new Set([...configuredModels,...result.suggestions])];
      refreshModelSuggestions();
      const pickerStatus=result.nativePickerUpdated
        ?'应用管理的原生模型选择器已更新，重启 CLI 或启动新会话后生效。'
        :'返回的 ID 已加入本次编辑的模型建议。';
      gatewayResult.textContent=`读取到 ${result.suggestions.length} 个模型 ID。${pickerStatus}${result.note?` ${result.note}`:''}`;
    }).catch(cause=>{
      if(!d.element.open)return;
      gatewayResult.classList.add('error-text');
      gatewayResult.textContent=`读取失败。${cause instanceof Error?cause.message:String(cause)}`;
    }).finally(()=>{
      gatewayButton.disabled=false;
      if(label)label.textContent='读取网关模型';
    });
  },'refresh','secondary');
  const canReadGateway=existing?.kind==='claude';
  gatewayButton.disabled=!canReadGateway;
  if(!canReadGateway)gatewayButton.title='先保存 Claude Agent 配置，再读取网关模型';
  const gatewayHint=el('p','settings-note',canReadGateway
    ?'读取使用已保存的 Agent 配置。若已修改此表单的环境变量，请先保存后重新打开。仅应用管理的配置会写入原生模型选择器，其他配置返回模型建议。'
    :'先保存 Claude Agent 配置，再读取网关返回的完整模型列表。');
  modelSection.append(model.label,suggestions,inherited,modelHint,discoveryRow,discoveryState,
    el('p','settings-note','Claude 原生发现仅收录 ID 包含 claude 或 anthropic 的模型。读取网关模型可获取 Magpie 等网关返回的完整列表。'),
    gatewayButton,gatewayResult,gatewayHint);
  d.body.append(modelSection,envLabel,el('p','settings-note','环境变量覆盖仅用于此 Agent 的新进程。密钥使用 Windows 本机加密保存。'));
  const refreshVisibility=()=>{modelSection.hidden=kind.value!=='claude';};
  kind.addEventListener('change',refreshVisibility);
  env.addEventListener('input',()=>{refreshModelSuggestions();refreshDiscovery();});
  refreshVisibility();refreshModelSuggestions();refreshDiscovery();
  const sourceId=existing?.kind==='claude'?existing.id:'claude';
  void call<{suggestions:string[];inheritedModel:string|null}>('agents.models',{agentId:sourceId}).then(choices=>{
    if(!d.element.open)return;
    configuredModels=[...new Set([...configuredModels,...(choices.suggestions||[])])];inheritedModel=choices.inheritedModel;refreshModelSuggestions();
  }).catch(()=>{if(d.element.open)inherited.textContent='本机模型建议暂不可用，可直接输入模型 ID。';});

  const resume=input('自定义恢复参数（可选，使用 {sessionId}）',existing?.resumeArguments||'');
  resume.input.placeholder='例如 --resume {sessionId}';d.body.append(resume.label);
  const saveButton=labeledButton('保存 Agent',()=>run(async()=>{
    const title=name.input.value.trim(),cmd=command.input.value.trim();
    if(!title||!cmd){toast('请填写名称和命令。',true);return;}
    const environment:Record<string,string>={};
    for(const line of env.value.split(/\r?\n/)){
      if(!line.trim())continue;
      const index=line.indexOf('=');
      if(index<1||!/^[A-Za-z_][A-Za-z0-9_]*$/.test(line.slice(0,index).trim())){
        toast('环境变量格式应为 KEY=VALUE，每行一项。',true);return;
      }
      environment[line.slice(0,index).trim()]=line.slice(index+1);
    }
    const updated:AgentProfile={
      id:existing?.id||`custom-${crypto.randomUUID()}`,name:title,command:cmd,
      kind:kind.value,env:environment,model:kind.value==='claude'?model.input.value.trim()||null:null,
      resumeArguments:resume.input.value.trim()||null,
      builtIn:existing?.builtIn||false,available:existing?.available||false
    };
    const next=existing?agents.map(agent=>agent.id===existing.id?updated:agent):[...agents,updated];
    saveButton.disabled=true;
    try{
      agents=await call<AgentProfile[]>('agents.save',{agents:next});
      d.close();done?.();renderSidebar();toast('Agent 启动配置已保存。');
    }finally{saveButton.disabled=false;}
  }),undefined,'primary');
  d.footer.append(labeledButton('取消',d.close),saveButton);
  if(focusModel){model.input.focus();model.input.select();}
}
function renderAppearanceSettings(panel:HTMLElement){
  const row=el('div','preference-row');row.append(el('div','', '界面主题'));const choices=el('div','segmented');for(const theme of ['dark','light']as const){const b=labeledButton(theme==='dark'?'深色':'浅色',()=>{state.theme=theme;applyTheme();save();panel.replaceChildren();renderAppearanceSettings(panel);},theme==='dark'?'moon':'sun');b.classList.toggle('active',state.theme===theme);choices.append(b);}row.append(choices);panel.append(row);
  const font=input('终端字号',String(state.fontSize),'number');font.input.min='10';font.input.max='24';font.input.addEventListener('change',()=>{state.fontSize=Math.max(10,Math.min(24,Number(font.input.value)||13));font.input.value=String(state.fontSize);applyTheme();save();});panel.append(font.label);
  const shortcuts=[['Ctrl Alt W','全局显示 / 隐藏 Weirdo'],['Ctrl Alt Shift W','全局打开 Agent 活动监视器'],['Ctrl P','快速切换项目、终端与 Agent'],['Ctrl Shift O','打开项目'],['Ctrl Shift T','新建终端'],['Ctrl Shift D / Ctrl Alt D','向右 / 向下分屏'],['Ctrl Shift E','最大化当前分屏'],['Ctrl Shift W','关闭当前终端'],['Ctrl Shift C / Ctrl Shift V','复制 / 粘贴'],['Ctrl F','搜索当前终端'],['Ctrl ,','打开设置']];const table=el('div','shortcut-table');for(const [key,description]of shortcuts){const item=el('div','shortcut-row');item.append(el('kbd','',key),el('span','',description));table.append(item);}panel.append(table,el('p','settings-note','关闭主窗口会保留后台任务。通过托盘重新打开。完整退出操作由托盘菜单提供。'));
}
function renderAboutSettings(panel:HTMLElement){panel.append(el('h3','about-title',`Weirdo for Windows ${appVersion}`),el('p','settings-description','为多 Agent 工作设计的 Windows 终端工作台。打开项目，让官方 CLI 保持原生体验。'),el('p','settings-note','Weirdo · MIT License\n终端字体 JetBrains Mono · 界面字体 Onest'));
  const result=el('div','update-result');const check=labeledButton('检查更新',()=>run(async()=>{check.disabled=true;try{const update=await call<{configured:boolean;available:boolean;currentVersion:string;version?:string;notes?:string;error?:string}>('updates.check');result.replaceChildren();if(update.error)result.append(el('p','error-text',update.error));else if(!update.configured)result.append(el('p','muted','尚未配置更新源。此构建不会从未知地址下载更新。'));else if(!update.available)result.append(el('p','muted',`当前已是更新源中的最新版本 (${update.currentVersion})。`));else {result.append(el('p','',`发现版本 ${update.version}`));if(update.notes)result.append(el('p','settings-note',update.notes));result.append(labeledButton('下载并安装',()=>run(async()=>{if(await confirm('安装更新','请先停止所有运行中的终端。安装包校验通过后，Weirdo 将关闭所有窗口和后台服务，退出后启动安装。项目布局和原生 Session ID 会保留。','继续'))await call('updates.install');}),'download','primary'));}}finally{check.disabled=false;}}),'refresh','secondary');panel.append(check,result);
  const feed=input('更新源（HTTPS URL 或本地 JSON 文件）');feed.input.placeholder='https://…/updates.json';panel.append(feed.label,labeledButton('保存更新源',()=>run(async()=>{await call('updates.configure',{feedUrl:feed.input.value.trim()});toast('更新源已保存。');}),undefined,'secondary'));
}

function openTerminalSearch(){const session=activeSession();if(!session)return;const d=dialog('搜索终端');const field=input('查找文本');d.body.append(field.label);const feedback=el('span','muted');d.footer.append(feedback,labeledButton('上一个',()=>{feedback.textContent=terminals.search(session.id,field.input.value,true)?'':'未找到';}),labeledButton('下一个',()=>{feedback.textContent=terminals.search(session.id,field.input.value)?'':'未找到';},undefined,'primary'));field.input.addEventListener('input',()=>{feedback.textContent=terminals.search(session.id,field.input.value)?'':'未找到';});field.input.addEventListener('keydown',event=>{if(event.key==='Enter'){event.preventDefault();feedback.textContent=terminals.search(session.id,field.input.value,event.shiftKey)?'':'未找到';}});field.input.focus();}

function mergeSession(info:SessionInfo){const existing=sessions.get(info.id);if(existing)Object.assign(existing,info);else sessions.set(info.id,{...info});}
function onOutput(chunk:Chunk){const session=sessions.get(chunk.sessionId);if(session){session.lastOutputAt=new Date().toISOString();session.bytesRead+=chunk.data.length;}}
on<SessionInfo[]>('sessions.changed',items=>{if(!Array.isArray(items))return;const previous=new Set(liveIds);liveIds.clear();for(const item of items){mergeSession(item);liveIds.add(item.id);}const layoutChanged=items.some(item=>!previous.has(item.id))||[...previous].some(id=>!liveIds.has(id));if(booted){if(layoutChanged)scheduleRender();else updateActivity();}});
on<SessionInfo>('terminal.exit',info=>{mergeSession(info);if(booted)scheduleRender();});
on<AgentProfile[]>('agents.changed',values=>{
  if(!Array.isArray(values))return;
  agents=values;
  if(booted)scheduleRender();
});
on<{state:Partial<AppState>|null}>('state.changed',payload=>{
  if(!booted||monitorMode)return;
  // Another window owns this write. Reconcile layouts without writing it back.
  clearTimeout(saveTimer);
  saveTimer=undefined;
  const previousWorkspace=state.activeWorkspaceId;
  const previousPanes=new Map(state.workspaces.map(w=>[w.id,w.activePaneId]));
  const previousTabs=new Map(state.workspaces.flatMap(w=>panes(w.tree).map(p=>[p.id,p.activeTabId] as const)));
  const next=restoreState(payload.state);
  if(next.workspaces.some(w=>w.id===previousWorkspace))next.activeWorkspaceId=previousWorkspace;
  for(const workspace of next.workspaces){const selected=previousPanes.get(workspace.id);if(selected&&findPane(workspace.tree,selected))workspace.activePaneId=selected;for(const pane of panes(workspace.tree)){const tab=previousTabs.get(pane.id);if(tab&&pane.tabs.includes(tab))pane.activeTabId=tab;}}
  for(const snapshot of next.sessionSnapshots)if(!sessions.has(snapshot.id))mergeSession({...snapshot,running:false});
  const changed=JSON.stringify({...state,sessionSnapshots:[]})!==JSON.stringify({...next,sessionSnapshots:[]});
  // Equal layouts retain the objects captured by the currently mounted controls.
  state.sessionSnapshots=next.sessionSnapshots;
  if(changed){state=next;zoomPaneId=null;applyTheme();render();void inspectWorkspace().catch(()=>{});}
});
on<{message:string}>('host.error',payload=>error(payload.message));
on<{maximized:boolean}>('window.maximized',payload=>setMaximized(payload.maximized));
on<{message?:string}>('broker.disconnected',payload=>{
  connectionActive=false;
  if(booted&&!monitorMode)renderStatus();
  error(payload?.message||'终端服务连接已中断。请重新打开 Weirdo 以连接已有会话。');
});
on<{version:string}>('updates.available',payload=>toast(`Weirdo ${payload.version} 已可用。可在设置 → 更新中安装。`));
on<{sessionId:string}>('focus.session',payload=>{for(const workspace of state.workspaces)for(const pane of panes(workspace.tree))if(pane.tabs.includes(payload.sessionId)){selectSession(workspace,pane,payload.sessionId);return;}});
function updateActivity(){if(monitorMode){renderMonitor();return;}for(const node of document.querySelectorAll<HTMLElement>('[data-session-id]')){const session=sessions.get(node.dataset.sessionId!);if(!session)continue;const dot=node.querySelector('.status-dot');if(dot)dot.className=`status-dot ${activity(session).kind}`;}const workspace=activeWorkspace();if(workspace)for(const footer of content.querySelectorAll<HTMLElement>('[data-status-for]')){const pane=findPane(workspace.tree,footer.dataset.statusFor!);if(pane)renderPaneStatus(footer,pane,workspace);}renderStatus();}

function renderMonitor(){
  app.className='monitor-app';const header=el('header','monitor-header');header.append(icon('monitor',18),el('h1','','Agent 活动'),labeledButton('工作台',()=>run(()=>call('window.show')),'arrow','small-button'),windowControls());bindTitlebar(header);const list=el('div','monitor-list');const items=[...sessions.values()].filter(s=>liveIds.has(s.id)).sort((a,b)=>Number(b.running)-Number(a.running)||Date.parse(b.startedAt)-Date.parse(a.startedAt));
  if(!items.length)list.append(el('div','monitor-empty','没有运行中的终端。\n打开工作台启动一个 Agent。'));
  for(const session of items){const item=el('article','monitor-item'),status=activity(session);const title=el('div','monitor-item-title');title.append(agentIcon(session.agentId),el('strong','truncate',session.title),el('i',`status-dot ${status.kind}`));const detail=el('div','monitor-detail');detail.append(el('span','',status.label),el('span','',`PID ${session.processId}`));const path=el('div','monitor-path',compactPath(session.cwd));path.title=session.cwd;item.append(title,path,detail);if(session.lastOutputAt){const elapsed=Math.max(0,Math.round((Date.now()-Date.parse(session.lastOutputAt))/1000));item.append(el('div','monitor-output-age',`最近输出 ${elapsed<60?`${elapsed} 秒`:`${Math.floor(elapsed/60)} 分钟`}前`));}list.append(item);}
  const footer=el('footer','monitor-footer','进程与输出状态 · 不推断模型思考或权限审批');app.replaceChildren(header,list,footer);
}

document.addEventListener('keydown',event=>{
  if(event.key==='Escape'){closeMenu();return;}if(document.querySelector('dialog[open]'))return;
  const key=event.code;if(event.ctrlKey&&key==='KeyP'){event.preventDefault();openQuickOpen();return;}if(event.ctrlKey&&key==='Comma'){event.preventDefault();openSettings();return;}if(event.ctrlKey&&key==='KeyF'){event.preventDefault();openTerminalSearch();return;}
  if(event.ctrlKey&&event.shiftKey&&key==='KeyO'){event.preventDefault();run(()=>openWorkspace());return;}
  if(event.ctrlKey&&event.shiftKey&&key==='KeyT'){event.preventDefault();if(activeWorkspace())run(()=>launch('shell'));else run(()=>openWorkspace());return;}
  if(event.ctrlKey&&event.shiftKey&&key==='KeyN'){event.preventDefault();run(()=>call('window.new'));return;}
  const workspace=activeWorkspace(),pane=activePane();if(!workspace||!pane)return;
  if(event.ctrlKey&&key==='KeyD'&&(event.shiftKey||event.altKey)){event.preventDefault();run(()=>split(workspace,pane,event.altKey?'vertical':'horizontal'));return;}
  if(event.ctrlKey&&event.shiftKey&&key==='KeyE'){event.preventDefault();zoomPaneId=zoomPaneId?null:pane.id;renderWorkspace();focusTerminal();return;}
  if(event.ctrlKey&&event.shiftKey&&key==='KeyW'){event.preventDefault();if(pane.activeTabId){const id=pane.activeTabId;run(()=>closeSession(id));}return;}
  if(event.ctrlKey&&key==='Tab'){event.preventDefault();const index=pane.tabs.indexOf(pane.activeTabId||'');const id=pane.tabs[(index+(event.shiftKey?-1:1)+pane.tabs.length)%pane.tabs.length];if(id)selectSession(workspace,pane,id);}
});
window.addEventListener('beforeunload',()=>{if(booted&&!monitorMode)void flushPendingSave().catch(()=>{});});

async function bootstrap(){
  if(!isDesktop){app.className='desktop-required';app.append(logoImage('welcome-logo'),el('h1','','请打开 Weirdo Windows 桌面应用'),el('p','','真实终端由 Windows ConPTY 提供。此浏览器页面没有桌面连接。'));return;}
  const loading=el('div','boot-message');loading.append(logoImage('welcome-logo'),el('p','','正在连接本机终端…'));
  app.className='boot-shell';app.append(startupTitlebar(),loading);
  try{const data=await call<Bootstrap>('bootstrap');state=restoreState(data.state);agents=data.agents||[];appVersion=data.appVersion||appVersion;homePath=data.homePath||'';for(const snapshot of state.sessionSnapshots)mergeSession({...snapshot,running:false});for(const session of data.sessions||[]){mergeSession(session);liveIds.add(session.id);}
    const assigned=new Set(state.workspaces.flatMap(w=>panes(w.tree).flatMap(p=>p.tabs)));
    for(const session of data.sessions||[])if(!assigned.has(session.id)){let workspace=state.workspaces.find(w=>w.id===session.workspaceId)||state.workspaces.find(w=>w.path.toLowerCase()===session.cwd.toLowerCase());if(!workspace){const pane=newPane();workspace={id:session.workspaceId||crypto.randomUUID(),name:baseName(session.cwd),path:session.cwd,pinned:false,tree:pane,activePaneId:pane.id};state.workspaces.push(workspace);}const pane=panes(workspace.tree)[0];pane.tabs.push(session.id);pane.activeTabId ||= session.id;}
    if(!state.activeWorkspaceId&&state.workspaces.length)state.activeWorkspaceId=state.workspaces[0].id;
    terminals.configureWindows(data.windowsBuild);applyTheme();booted=true;app.className='';if(!monitorMode)setupShell();render();void refreshWindowState().catch(error);for(const warning of data.warnings||[])toast(warning,true);if(!monitorMode){void inspectWorkspace().catch(error);focusTerminal();}
    setInterval(updateActivity,1500);setInterval(()=>{if(!monitorMode&&document.visibilityState==='visible')void inspectWorkspace().catch(()=>{});},10000);
  }catch(cause){app.replaceChildren();const failure=el('div','boot-failure');failure.append(el('h1','','无法连接终端服务'),el('p','',cause instanceof Error?cause.message:String(cause)),labeledButton('重试',()=>location.reload(),'refresh','primary'));app.className='boot-shell';app.append(startupTitlebar(),failure);}
}
void bootstrap().then(async()=>{
  if(!booted)return;
  await document.fonts.ready;
  await new Promise<void>(resolve=>requestAnimationFrame(()=>resolve()));
  await call('window.ready').catch(error);
});
