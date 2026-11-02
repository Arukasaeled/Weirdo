import {Terminal, type ITheme} from '@xterm/xterm';
import {FitAddon} from '@xterm/addon-fit';
import {SearchAddon} from '@xterm/addon-search';
import {WebglAddon} from '@xterm/addon-webgl';
import {Unicode11Addon} from '@xterm/addon-unicode11';
import {call,on} from './bridge';
import type {Chunk,Replay,SessionInfo} from './types';
import {OutputOrder} from './output-order';

// GitHub terminal palettes adapted from Weirdo's bundled Shiki themes (MIT).
export const darkTheme:ITheme={background:'#252930',foreground:'#e6edf3',cursor:'#e6edf3',selectionBackground:'#435365',black:'#484f58',red:'#ff7b72',green:'#3fb950',yellow:'#d29922',blue:'#58a6ff',magenta:'#bc8cff',cyan:'#39c5cf',white:'#b1bac4',brightBlack:'#6e7681',brightRed:'#ffa198',brightGreen:'#56d364',brightYellow:'#e3b341',brightBlue:'#79c0ff',brightMagenta:'#d2a8ff',brightCyan:'#56d4dd',brightWhite:'#ffffff'};
export const lightTheme:ITheme={background:'#ffffff',foreground:'#24292f',cursor:'#24292f',selectionBackground:'#b6d7fb',black:'#24292f',red:'#cf222e',green:'#116329',yellow:'#4d2d00',blue:'#0969da',magenta:'#8250df',cyan:'#1b7c83',white:'#6e7781',brightBlack:'#57606a',brightRed:'#a40e26',brightGreen:'#1a7f37',brightYellow:'#633c01',brightBlue:'#218bff',brightMagenta:'#a475f9',brightCyan:'#3192aa',brightWhite:'#8c959f'};
interface Entry {terminal:Terminal;fit:FitAddon;search:SearchAddon;element:HTMLDivElement;order:OutputOrder;webgl?:WebglAddon;webglTried:boolean;lastCols:number;lastRows:number}
export class Terminals {
  private entries=new Map<string,Entry>();
  private theme:'dark'|'light'='dark';
  private fontSize=13;
  private windowsBuild?:number;
  constructor(private parking:HTMLElement,private error:(error:unknown)=>void,private pasteConfirm:(text:string)=>Promise<boolean>,private output:(chunk:Chunk)=>void) {
    on<Chunk>('terminal.output',chunk=>{
      this.output(chunk);
      const entry=this.entries.get(chunk.sessionId);
      // Unopened tabs obtain the broker's bounded replay when first attached.
      if(!entry)return;
      entry.order.accept(chunk);
    });
  }
  ensure(session:SessionInfo,exists:boolean):Entry {
    let entry=this.entries.get(session.id);if(entry)return entry;
    const element=document.createElement('div');element.className='terminal';element.dataset.sessionId=session.id;this.parking.append(element);
    const terminal=new Terminal({cols:session.cols||100,rows:session.rows||30,fontFamily:'"JetBrains Mono", "Cascadia Mono", Consolas, "Microsoft YaHei", monospace',fontSize:this.fontSize,lineHeight:1.15,letterSpacing:0,cursorBlink:true,cursorStyle:'bar',allowProposedApi:true,scrollback:5000,convertEol:false,theme:this.theme==='dark'?darkTheme:lightTheme,windowsPty:{backend:'conpty',buildNumber:this.windowsBuild},rescaleOverlappingGlyphs:true,drawBoldTextInBrightColors:false,screenReaderMode:false});
    // The supported Unicode addon needs xterm's proposed Unicode API. Register
    // widths before any replay or live data reaches the terminal parser.
    terminal.loadAddon(new Unicode11Addon());terminal.unicode.activeVersion='11';
    const fit=new FitAddon(),search=new SearchAddon();terminal.loadAddon(fit);terminal.loadAddon(search);terminal.open(element);
    const order=new OutputOrder(data=>terminal.write(data));
    entry={terminal,fit,search,element,order,webglTried:false,lastCols:session.cols,lastRows:session.rows};this.entries.set(session.id,entry);
    terminal.onData(data=>{if(session.running)void call('session.input',{sessionId:session.id,data}).catch(this.error);});
    terminal.onBinary(data=>{if(session.running)void call('session.input',{sessionId:session.id,data}).catch(this.error);});
    terminal.attachCustomKeyEventHandler(event=>{
      if(event.type!=='keydown')return true;
      if(event.ctrlKey&&event.shiftKey&&event.code==='KeyC'){event.preventDefault();if(terminal.hasSelection())void call('clipboard.write',{text:terminal.getSelection()}).catch(this.error);return false;}
      if((event.ctrlKey&&event.shiftKey&&event.code==='KeyV')||(event.shiftKey&&event.code==='Insert')){event.preventDefault();void this.paste(session.id).catch(this.error);return false;}
      if(event.ctrlKey&&(event.code==='Tab'||event.code==='KeyP'||event.code==='Comma'||event.code==='KeyF'||(event.altKey&&event.code==='KeyD')||(event.shiftKey&&['KeyT','KeyD','KeyE','KeyW','KeyO','KeyN'].includes(event.code))))return false;
      return true;
    });
    element.addEventListener('contextmenu',event=>{event.preventDefault();if(terminal.hasSelection())void call('clipboard.write',{text:terminal.getSelection()}).catch(this.error);else void this.paste(session.id).catch(this.error);});
    element.addEventListener('pointerdown',event=>{if(event.button===1){event.preventDefault();void this.paste(session.id).catch(this.error);}});
    if(exists)void call<Replay>('session.replay',{sessionId:session.id}).then(replay=>order.replay(replay)).catch(error=>{order.failed();this.error(error);});else order.failed();
    return entry;
  }
  attach(session:SessionInfo,parent:HTMLElement,exists:boolean) {const entry=this.ensure(session,exists);parent.append(entry.element);this.fit(session.id);}
  parkAll(){for(const entry of this.entries.values())this.parking.append(entry.element);}
  focus(id:string){this.entries.get(id)?.terminal.focus();}
  fit(id:string){const entry=this.entries.get(id);if(!entry||entry.element.clientWidth<50||entry.element.clientHeight<30||!entry.element.checkVisibility())return;
    try{entry.fit.fit();if(!entry.webglTried){entry.webglTried=true;try{const webgl=new WebglAddon();webgl.onContextLoss(()=>{webgl.dispose();entry.webgl=undefined;});entry.terminal.loadAddon(webgl);entry.webgl=webgl;}catch{/* xterm's DOM renderer remains functional. */}}
      const {cols,rows}=entry.terminal;if(cols!==entry.lastCols||rows!==entry.lastRows){entry.lastCols=cols;entry.lastRows=rows;void call('session.resize',{sessionId:id,cols,rows}).catch(()=>{});}
    }catch(error){this.error(error);}
  }
  fitAll(){for(const id of this.entries.keys())this.fit(id);}
  configureWindows(build?:number){this.windowsBuild=Number.isInteger(build)&&build!>0?build:undefined;for(const entry of this.entries.values())entry.terminal.options.windowsPty={backend:'conpty',buildNumber:this.windowsBuild};}
  configure(theme:'dark'|'light',fontSize:number){this.theme=theme;this.fontSize=fontSize;for(const entry of this.entries.values()){entry.terminal.options.theme=theme==='dark'?darkTheme:lightTheme;entry.terminal.options.fontSize=fontSize;}requestAnimationFrame(()=>this.fitAll());}
  dispose(id:string){const entry=this.entries.get(id);if(entry){entry.terminal.dispose();entry.element.remove();this.entries.delete(id);}}
  async paste(id:string,text?:string){const entry=this.entries.get(id);if(!entry)return;const value=text??await call<string>('clipboard.read');if(!value)return;
    if(/[\r\n]/.test(value)&&!entry.terminal.modes.bracketedPasteMode&&!await this.pasteConfirm(value))return;
    entry.terminal.paste(value);entry.terminal.focus();
  }
  insert(id:string,text:string){const entry=this.entries.get(id);if(entry){entry.terminal.paste(text);entry.terminal.focus();}}
  search(id:string,query:string,backward=false){const addon=this.entries.get(id)?.search;return backward?addon?.findPrevious(query,{incremental:true}):addon?.findNext(query,{incremental:true});}
  text(id:string){const entry=this.entries.get(id);if(!entry)return '';const buffer=entry.terminal.buffer.active;const rows=[];for(let i=0;i<buffer.length;i++)rows.push(buffer.getLine(i)?.translateToString(true)||'');return rows.join('\n');}
}
