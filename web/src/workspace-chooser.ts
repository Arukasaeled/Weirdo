import {call} from './bridge';
import {button,dialog,el,icon,labeledButton} from './ui';
import {directoryKey} from './directory-cache';
import type {AppState,Workspace} from './types';

export interface WorkspaceLocation {path:string;name:string}
interface ChooserContext {
  workspaces:()=>Workspace[];
  recent:()=>AppState['recent'];
  initialPath?:string;
}
interface Choice {name:string;path:string;group:string;icon:string}

function looksLikePath(value:string):boolean {
  const path=value.trim().replace(/^(["'])(.*)\1$/,'$2');
  return /^(?:[a-z]:[\\/]|\\\\|~(?:[\\/]|$)|%[^%]+%)/i.test(path);
}

export function chooseWorkspace(context:ChooserContext):Promise<WorkspaceLocation|null> {
  return new Promise(resolve=>{
    const modal=dialog('打开项目',true);
    modal.element.classList.add('workspace-chooser');
    let result:WorkspaceLocation|null=null;
    let selected=0;
    let choices:Choice[]=[];
    let busy=false;
    modal.element.addEventListener('close',()=>resolve(result),{once:true});

    const searchRow=el('div','chooser-search-row');
    const search=el('input','chooser-search');
    search.placeholder='搜索项目，或粘贴文件夹路径…';
    search.setAttribute('aria-label','搜索项目或输入文件夹路径');
    search.setAttribute('role','combobox');
    search.setAttribute('aria-expanded','true');
    search.autocomplete='off';search.spellcheck=false;
    const list=el('div','chooser-results');
    list.id=`workspace-choices-${crypto.randomUUID()}`;
    list.setAttribute('role','listbox');list.setAttribute('aria-label','项目列表');
    search.setAttribute('aria-controls',list.id);
    const status=el('p','chooser-status');status.setAttribute('aria-live','polite');
    const browse=labeledButton('浏览文件夹…',()=>void browseDirectory(),'folder','secondary');
    searchRow.append(icon('search',17),search,browse);
    const hint=el('p','chooser-hint','固定、已打开和最近项目 · ↑ ↓ 选择 · Enter 打开');
    modal.body.append(searchRow,hint,list,status);
    const open=labeledButton('打开所选项目',()=>void activate(),'arrow','primary');
    modal.footer.append(labeledButton('取消',modal.close),open);

    function renderSelection(){
      for(const [index,node] of [...list.children].entries()){
        if(node.getAttribute('role')!=='option')continue;
        node.classList.toggle('selected',index===selected);
        node.setAttribute('aria-selected',String(index===selected));
      }
      const current=choices.length?list.children[selected]:undefined;
      if(current)search.setAttribute('aria-activedescendant',current.id);
      else search.removeAttribute('aria-activedescendant');
      open.disabled=busy||!choices.length;
    }

    function renderChoices(){
      const query=search.value.trim().toLocaleLowerCase();
      const opened=[...context.workspaces()].sort((a,b)=>Number(b.pinned)-Number(a.pinned));
      const known=new Set(opened.map(workspace=>directoryKey(workspace.path)));
      const all:Choice[]=opened.map(workspace=>({name:workspace.name,path:workspace.path,group:workspace.pinned?'固定':'已打开',icon:workspace.pinned?'pin':'folder'}));
      for(const recent of context.recent())if(!known.has(directoryKey(recent.path))){
        known.add(directoryKey(recent.path));
        all.push({name:recent.name,path:recent.path,group:'最近',icon:'history'});
      }
      choices=all.filter(choice=>!query||`${choice.name} ${choice.path}`.toLocaleLowerCase().includes(query));
      if(looksLikePath(search.value))choices.unshift({name:'打开此文件夹',path:search.value.trim(),group:'路径',icon:'folder'});
      selected=Math.min(selected,Math.max(choices.length-1,0));
      list.replaceChildren();
      choices.forEach((choice,index)=>{
        const row=button(choice.name,()=>{selected=index;void activate();});
        row.replaceChildren();row.className='chooser-result';row.id=`${list.id}-${index}`;
        row.setAttribute('role','option');row.title=choice.path;
        const detail=el('span','chooser-result-detail');
        detail.append(el('strong','',choice.name),el('span','chooser-path',choice.path));
        row.append(icon(choice.icon,17),detail,el('span','chooser-group',choice.group));
        row.addEventListener('pointerenter',()=>{selected=index;renderSelection();});
        list.append(row);
      });
      if(!choices.length){
        const empty=el('p','chooser-empty',query?'没有匹配的项目。可粘贴完整路径或浏览文件夹。':'选择一个文件夹，开始你的第一个项目。');
        empty.setAttribute('role','status');list.append(empty);
      }
      renderSelection();
    }

    async function resolveAndClose(path:string){
      if(busy)return;
      busy=true;search.readOnly=true;browse.disabled=true;open.disabled=true;
      status.classList.remove('error-text');status.textContent='正在打开文件夹…';
      try{
        const location=await call<WorkspaceLocation>('workspace.resolve',{path});
        if(!modal.element.open)return;
        result=location;modal.close();
      }catch(cause){
        if(!modal.element.open)return;
        status.classList.add('error-text');status.textContent=cause instanceof Error?cause.message:String(cause);
        search.focus();
      }finally{
        busy=false;search.readOnly=false;browse.disabled=false;renderSelection();
      }
    }

    async function activate(){
      if(choices[selected])await resolveAndClose(choices[selected].path);
    }

    async function browseDirectory(){
      if(busy)return;
      browse.disabled=true;status.textContent='';
      try{
        const path=await call<string|null>('dialog.folder',{path:looksLikePath(search.value)?search.value:context.initialPath});
        if(path&&modal.element.open)await resolveAndClose(path);
      }catch(cause){
        status.classList.add('error-text');status.textContent=cause instanceof Error?cause.message:String(cause);
      }finally{
        browse.disabled=false;if(modal.element.open)search.focus();
      }
    }

    search.addEventListener('input',()=>{selected=0;status.textContent='';renderChoices();});
    modal.element.addEventListener('keydown',event=>{
      const inList=Boolean((event.target as Element).closest('.chooser-result'));
      if(event.target!==search&&!inList)return;
      if(event.key==='ArrowDown'||event.key==='ArrowUp'){
        event.preventDefault();
        if(!choices.length)return;
        selected=Math.max(0,Math.min(choices.length-1,selected+(event.key==='ArrowDown'?1:-1)));
        renderSelection();list.children[selected]?.scrollIntoView({block:'nearest'});
        if(inList)(list.children[selected] as HTMLElement)?.focus();
      }else if(event.key==='Enter'&&event.target===search){
        event.preventDefault();void activate();
      }
    });
    renderChoices();search.focus();
  });
}
