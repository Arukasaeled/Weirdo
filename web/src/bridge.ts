type HostMessage = { id?:string; ok?:boolean; result?:unknown; error?:string; type?:string; payload?:unknown };
interface WebView { postMessage(message:unknown):void; addEventListener(type:'message', listener:(event:MessageEvent<HostMessage>)=>void):void }
declare global { interface Window { chrome?:{webview?:WebView} } }
const listeners = new Map<string,Set<(data:any)=>void>>();
const pending = new Map<string,{resolve:(value:any)=>void;reject:(error:Error)=>void;timer:ReturnType<typeof setTimeout>}>();
export const isDesktop = Boolean(window.chrome?.webview);
window.chrome?.webview?.addEventListener('message', event => {
  const message = event.data;
  if (!message || typeof message !== 'object') return;
  if (message.id && pending.has(message.id)) {
    const request = pending.get(message.id)!; pending.delete(message.id); clearTimeout(request.timer);
    if (message.ok) request.resolve(message.result); else request.reject(new Error(message.error || '操作未完成'));
  } else if (message.type) for (const handler of listeners.get(message.type) || []) handler(message.payload);
});
export function call<T=void>(type:string,payload:unknown={}):Promise<T> {
  if (!window.chrome?.webview) return Promise.reject(new Error('请从 Kooky Windows 桌面应用打开。浏览器预览无法连接本机终端。'));
  return new Promise((resolve,reject)=> {
    const id = crypto.randomUUID();
    const timeout=type==='updates.install'?600_000:type==='dialog.folder'?300_000:type==='updates.check'?120_000:30_000;
    const timer = setTimeout(()=>{pending.delete(id);reject(new Error(`${type} 请求超时，请检查桌面连接。`));},timeout);
    pending.set(id,{resolve,reject,timer});
    window.chrome!.webview!.postMessage({id,type,payload});
  });
}
export function on<T>(type:string,handler:(data:T)=>void):()=>void {
  const set = listeners.get(type) || new Set(); set.add(handler); listeners.set(type,set);
  return ()=>set.delete(handler);
}
