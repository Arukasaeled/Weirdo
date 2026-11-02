import type {FileEntry} from './types.ts';

export function directoryKey(path:string):string {
  return path.replace(/\\/g,'/').replace(/\/+$/,'').toLowerCase();
}

/** Directory reads are shared until refresh. Older reads cannot refill a refreshed cache. */
export class DirectoryCache {
  private entries=new Map<string,FileEntry[]>();
  private pending=new Map<string,Promise<FileEntry[]>>();
  private fetchDirectory:(path:string)=>Promise<FileEntry[]>;
  generation=0;

  constructor(fetchDirectory:(path:string)=>Promise<FileEntry[]>) {
    this.fetchDirectory=fetchDirectory;
  }

  peek(path:string):FileEntry[]|undefined {
    return this.entries.get(directoryKey(path));
  }

  load(path:string):Promise<FileEntry[]> {
    const key=directoryKey(path);
    const cached=this.entries.get(key);
    if(cached)return Promise.resolve(cached);
    const pending=this.pending.get(key);
    if(pending)return pending;
    const generation=this.generation;
    const request=this.fetchDirectory(path).then(entries=>{
      if(generation===this.generation)this.entries.set(key,entries);
      return entries;
    }).finally(()=>{
      if(this.pending.get(key)===request)this.pending.delete(key);
    });
    this.pending.set(key,request);
    return request;
  }

  invalidate():void {
    this.generation++;
    this.entries.clear();
    this.pending.clear();
  }
}
