import type {Chunk,Replay} from './types.ts';

/** Sequences are broker-owned. A replay snapshot may race with queued live chunks. */
export class OutputOrder {
  sequence=0;
  private awaitingReplay=true;
  private queued:Chunk[]=[];
  private write:(data:string)=>void;
  constructor(write:(data:string)=>void){this.write=write;}
  accept(chunk:Chunk){if(this.awaitingReplay){this.queued.push(chunk);return;}if(chunk.sequence<=this.sequence)return;this.sequence=chunk.sequence;this.write(chunk.data);}
  replay(snapshot:Replay){this.write(snapshot.data);this.sequence=snapshot.sequence;this.awaitingReplay=false;for(const chunk of this.queued.sort((a,b)=>a.sequence-b.sequence))this.accept(chunk);this.queued=[];}
  failed(){this.awaitingReplay=false;for(const chunk of this.queued.sort((a,b)=>a.sequence-b.sequence))this.accept(chunk);this.queued=[];}
}
