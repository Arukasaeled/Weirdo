import {test} from 'node:test';
import assert from 'node:assert/strict';
import {DirectoryCache} from '../src/directory-cache.ts';
import type {FileEntry} from '../src/types.ts';

function file(name:string):FileEntry {
  return {name,path:`D:\\project\\${name}`,isDirectory:false,size:1};
}

test('concurrent reads of the same Windows directory share the request and resolved result',async()=>{
  let reads=0;
  let complete!:(entries:FileEntry[])=>void;
  const cache=new DirectoryCache(()=>{reads++;return new Promise(resolve=>{complete=resolve;});});
  const first=cache.load('D:\\Project');
  const second=cache.load('d:/project/');
  assert.equal(first,second);
  complete([file('one.txt')]);
  await first;
  assert.equal((await cache.load('D:\\PROJECT'))[0].name,'one.txt');
  assert.equal(reads,1);
});

test('refresh keeps the newer listing when an older request finishes last',async()=>{
  const completions:Array<(entries:FileEntry[])=>void>=[];
  const cache=new DirectoryCache(()=>new Promise(resolve=>completions.push(resolve)));
  const oldRead=cache.load('D:\\project');
  cache.invalidate();
  const newRead=cache.load('D:\\project');
  completions[1]([file('new.txt')]);await newRead;
  completions[0]([file('removed.txt')]);await oldRead;
  assert.equal(cache.peek('D:\\project')?.[0].name,'new.txt');
});

test('a failed directory read can be retried without refreshing the whole tree',async()=>{
  let reads=0;
  const cache=new DirectoryCache(async()=>{
    reads++;
    if(reads===1)throw new Error('Directory temporarily unavailable');
    return [file('available.txt')];
  });
  await assert.rejects(cache.load('D:\\project'),/temporarily unavailable/);
  assert.equal((await cache.load('D:\\project'))[0].name,'available.txt');
  assert.equal(reads,2);
});
