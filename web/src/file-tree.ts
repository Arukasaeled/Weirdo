import {call} from './bridge';
import {DirectoryCache,directoryKey} from './directory-cache';
import {button,el,icon,labeledButton,menu} from './ui';
import type {FileEntry,Workspace} from './types';

interface TreeCallbacks {
  canInsert:()=>boolean;
  insert:(path:string)=>void;
  quote:(path:string)=>string;
  error:(cause:unknown)=>void;
}
interface VisibleEntry {entry:FileEntry;depth:number;parent:string}
interface TreeView {
  root:string;
  panel:HTMLDivElement;
  title:HTMLSpanElement;
  scroll:HTMLDivElement;
  tree:HTMLDivElement;
  refresh:HTMLButtonElement;
  cache:DirectoryCache;
  expanded:Map<string,string>;
  pending:Set<string>;
  errors:Map<string,string>;
  selected:string|null;
  scrollTop:number;
  visible:VisibleEntry[];
  rows:Map<string,HTMLDivElement>;
}

export class FileTreePanel {
  private views=new Map<string,TreeView>();
  private mounted:TreeView|undefined;
  private callbacks:TreeCallbacks;

  constructor(callbacks:TreeCallbacks){this.callbacks=callbacks;}

  show(workspace:Workspace,container:HTMLElement):void {
    let view=this.views.get(workspace.id);
    if(view&&directoryKey(view.root)!==directoryKey(workspace.path)){
      view.cache.invalidate();this.views.delete(workspace.id);view=undefined;
    }
    if(!view){view=this.createView(workspace);this.views.set(workspace.id,view);}
    view.title.textContent=workspace.name;
    view.title.title=workspace.path;
    view.tree.setAttribute('aria-label',`${workspace.name} 文件`);
    if(this.mounted!==view||view.panel.parentElement!==container){
      this.hide();
      this.mounted=view;
      container.replaceChildren(view.panel);
      view.scroll.scrollTop=view.scrollTop;
    }
    this.updateActions(view);
    if(!view.cache.peek(view.root)&&!view.pending.size&&!view.errors.size)this.load(view,view.root);
  }

  hide():void {
    if(this.mounted){
      this.mounted.scrollTop=this.mounted.scroll.scrollTop;
      this.mounted=undefined;
    }
  }

  retain(workspaceIds:Set<string>):void {
    for(const [id,view] of this.views)if(!workspaceIds.has(id)){
      view.cache.invalidate();this.views.delete(id);
      if(this.mounted===view)this.mounted=undefined;
    }
  }

  private createView(workspace:Workspace):TreeView {
    const panel=el('div','file-tree-panel');
    const toolbar=el('div','tree-toolbar');
    const title=el('span','tree-title',workspace.name);
    const refresh=button('刷新文件列表，保留展开状态',()=>this.refresh(view),'refresh','icon-button');
    toolbar.append(title,refresh);
    const scroll=el('div','file-tree-scroll');
    const tree=el('div','file-tree');
    tree.setAttribute('role','tree');tree.setAttribute('aria-label',`${workspace.name} 文件`);
    scroll.append(tree);
    const hint=el('div','tree-key-hint','↑ ↓ 选择 · Enter 插入文件路径');
    panel.append(toolbar,scroll,hint);
    const view:TreeView={
      root:workspace.path,panel,title,scroll,tree,refresh,
      cache:new DirectoryCache(path=>call<FileEntry[]>('workspace.files',{root:workspace.path,path})),
      expanded:new Map(),pending:new Set(),errors:new Map(),selected:null,scrollTop:0,
      visible:[],rows:new Map()
    };
    scroll.addEventListener('scroll',()=>{if(panel.isConnected)view.scrollTop=scroll.scrollTop;});
    this.render(view);
    return view;
  }

  private load(view:TreeView,path:string):void {
    const key=directoryKey(path);
    if(view.pending.has(key)||view.cache.peek(path))return;
    const generation=view.cache.generation;
    view.pending.add(key);view.errors.delete(key);
    view.refresh.disabled=true;
    void view.cache.load(path).catch(cause=>{
      if(generation===view.cache.generation)view.errors.set(key,cause instanceof Error?cause.message:String(cause));
    }).finally(()=>{
      if(generation!==view.cache.generation)return;
      view.pending.delete(key);
      this.render(view);
      view.refresh.disabled=view.pending.size>0;
    });
  }

  private refresh(view:TreeView):void {
    view.cache.invalidate();view.pending.clear();view.errors.clear();
    this.load(view,view.root);
    this.render(view);
  }

  private render(view:TreeView):void {
    const hadFocus=view.tree.contains(document.activeElement);
    const scrollTop=view.panel.isConnected?view.scroll.scrollTop:view.scrollTop;
    const fragment=document.createDocumentFragment();
    view.visible=[];view.rows.clear();
    this.appendDirectory(view,view.root,0,fragment);
    view.tree.replaceChildren(fragment);
    this.updateSelection(view);
    this.updateActions(view);
    view.scroll.scrollTop=scrollTop;
    view.scrollTop=scrollTop;
    if(hadFocus){
      const selected=view.selected?view.rows.get(view.selected):undefined;
      (selected||view.rows.values().next().value)?.focus({preventScroll:true});
    }
  }

  private appendDirectory(view:TreeView,path:string,depth:number,parent:DocumentFragment|HTMLElement):void {
    const entries=view.cache.peek(path);
    const key=directoryKey(path);
    if(!entries){
      const row=el('div','tree-message');row.style.paddingLeft=`${14+depth*13}px`;
      const failure=view.errors.get(key);
      if(failure){
        const text=el('span','',failure);text.title=failure;
        row.classList.add('tree-error');row.append(text,labeledButton('重试',()=>{this.load(view,path);this.render(view);},undefined,'small-button'));
      }else{
        row.textContent='读取目录…';this.load(view,path);
      }
      parent.append(row);return;
    }
    if(!entries.length){
      const empty=el('div','tree-message','空目录');empty.style.paddingLeft=`${14+depth*13}px`;parent.append(empty);return;
    }
    for(const entry of entries){
      view.visible.push({entry,depth,parent:path});
      parent.append(this.row(view,entry,depth,path));
      if(entry.isDirectory&&view.expanded.has(directoryKey(entry.path))){
        const children=el('div','file-children');children.setAttribute('role','group');
        this.appendDirectory(view,entry.path,depth+1,children);parent.append(children);
      }
    }
  }

  private row(view:TreeView,entry:FileEntry,depth:number,parent:string):HTMLDivElement {
    const key=directoryKey(entry.path);
    const row=el('div','file-row');row.dataset.filePath=entry.path;
    row.style.paddingLeft=`${8+depth*13}px`;
    row.setAttribute('role','treeitem');row.setAttribute('aria-level',String(depth+1));
    row.setAttribute('aria-label',entry.name);row.tabIndex=-1;
    row.title=`${entry.path}\n${entry.isDirectory?'双击展开目录':'双击打开文件，Enter 插入路径'}`;
    const arrow=el('span','file-chevron');
    if(entry.isDirectory){
      const expanded=view.expanded.has(key);
      row.setAttribute('aria-expanded',String(expanded));
      const toggle=button(`${expanded?'折叠':'展开'} ${entry.name}`,()=>{
        this.select(view,key,false);this.toggle(view,entry);
      },'chevron','file-expand');
      toggle.tabIndex=-1;toggle.classList.toggle('expanded',expanded);
      toggle.addEventListener('click',event=>event.stopPropagation());
      arrow.append(toggle);
    }
    row.append(arrow,icon(entry.isDirectory?'folder':'file',14),el('span','file-name',entry.name));
    const actions=el('span','file-actions');
    const insert=button(`插入路径 ${entry.name}`,()=>{
      this.select(view,key,false);this.callbacks.insert(entry.path);
    },'arrow','icon-button file-action');
    insert.dataset.insertPath='';insert.tabIndex=-1;insert.title='插入路径';
    const copy=button(`复制路径 ${entry.name}`,()=>{
      this.select(view,key,false);this.perform(()=>call('clipboard.write',{text:entry.path}));
    },'copy','icon-button file-action');
    copy.tabIndex=-1;copy.title='复制路径';
    for(const action of [insert,copy])action.addEventListener('click',event=>event.stopPropagation());
    actions.append(insert,copy);row.append(actions);
    row.addEventListener('click',()=>this.select(view,key,true));
    row.addEventListener('dblclick',event=>{
      if((event.target as Element).closest('button'))return;
      event.preventDefault();event.stopPropagation();
      if(entry.isDirectory)this.toggle(view,entry);
      else this.perform(()=>call('path.open',{path:entry.path}));
    });
    row.addEventListener('keydown',event=>this.onKey(view,entry,parent,event));
    row.addEventListener('contextmenu',event=>{
      event.preventDefault();this.select(view,key,false);this.contextMenu(entry,{x:event.clientX,y:event.clientY});
    });
    row.draggable=true;
    row.addEventListener('dragstart',event=>{
      event.dataTransfer!.setData('text/plain',this.callbacks.quote(entry.path));
      event.dataTransfer!.setData('application/x-weirdo-path',entry.path);
      event.dataTransfer!.effectAllowed='copy';
    });
    view.rows.set(key,row);
    return row;
  }

  private toggle(view:TreeView,entry:FileEntry):void {
    const key=directoryKey(entry.path);
    if(view.expanded.has(key)){
      view.expanded.delete(key);
      if(view.selected?.startsWith(`${key}/`))view.selected=key;
    }else{
      view.expanded.set(key,entry.path);this.load(view,entry.path);
    }
    this.render(view);
  }

  private select(view:TreeView,key:string,focus:boolean):void {
    view.selected=key;this.updateSelection(view);
    if(focus){view.rows.get(key)?.focus({preventScroll:true});view.rows.get(key)?.scrollIntoView({block:'nearest'});}
  }

  private updateSelection(view:TreeView):void {
    const selected=view.selected&&view.rows.has(view.selected)?view.selected:view.rows.keys().next().value;
    for(const [key,row] of view.rows){
      row.classList.toggle('selected',key===view.selected);
      row.setAttribute('aria-selected',String(key===view.selected));
      row.tabIndex=key===selected?0:-1;
    }
  }

  private updateActions(view:TreeView):void {
    const canInsert=this.callbacks.canInsert();
    for(const button of view.tree.querySelectorAll<HTMLButtonElement>('[data-insert-path]'))button.disabled=!canInsert;
  }

  private onKey(view:TreeView,entry:FileEntry,parent:string,event:KeyboardEvent):void {
    if((event.target as Element).closest('button')&&['Enter',' '].includes(event.key))return;
    const key=directoryKey(entry.path);
    const index=view.visible.findIndex(item=>directoryKey(item.entry.path)===key);
    if(['ArrowUp','ArrowDown','Home','End'].includes(event.key)){
      event.preventDefault();event.stopPropagation();
      let next=index+(event.key==='ArrowDown'?1:-1);
      if(event.key==='Home')next=0;
      if(event.key==='End')next=view.visible.length-1;
      const target=view.visible[Math.max(0,Math.min(view.visible.length-1,next))];
      if(target)this.select(view,directoryKey(target.entry.path),true);
    }else if(event.key==='ArrowRight'){
      event.preventDefault();event.stopPropagation();
      if(!entry.isDirectory)return;
      if(!view.expanded.has(key))this.toggle(view,entry);
      else{
        const child=view.visible[index+1];
        if(child&&directoryKey(child.parent)===key)this.select(view,directoryKey(child.entry.path),true);
      }
    }else if(event.key==='ArrowLeft'){
      event.preventDefault();event.stopPropagation();
      if(entry.isDirectory&&view.expanded.has(key))this.toggle(view,entry);
      else if(view.rows.has(directoryKey(parent)))this.select(view,directoryKey(parent),true);
    }else if(event.key==='Enter'){
      event.preventDefault();event.stopPropagation();
      if(entry.isDirectory)this.toggle(view,entry);
      else this.callbacks.insert(entry.path);
    }else if(event.ctrlKey&&event.code==='KeyC'){
      event.preventDefault();event.stopPropagation();this.perform(()=>call('clipboard.write',{text:entry.path}));
    }else if(event.key==='ContextMenu'||(event.shiftKey&&event.key==='F10')){
      event.preventDefault();const rect=view.rows.get(key)!.getBoundingClientRect();
      this.contextMenu(entry,{x:rect.left+20,y:rect.bottom});
    }
  }

  private contextMenu(entry:FileEntry,point:{x:number;y:number}):void {
    menu([
      {label:'插入路径到终端',icon:'terminal',disabled:!this.callbacks.canInsert(),action:()=>this.callbacks.insert(entry.path)},
      {label:'复制路径',icon:'copy',action:()=>this.perform(()=>call('clipboard.write',{text:entry.path}))},
      {label:entry.isDirectory?'在资源管理器中打开':'打开文件',icon:'folder',action:()=>this.perform(()=>call('path.open',{path:entry.path}))}
    ],point);
  }

  private perform(action:()=>Promise<unknown>):void {
    void action().catch(this.callbacks.error);
  }
}
