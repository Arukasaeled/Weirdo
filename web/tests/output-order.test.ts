import {test} from 'node:test';
import assert from 'node:assert/strict';
import {OutputOrder} from '../src/output-order.ts';
test('live output racing the replay is not duplicated or dropped',()=>{
  let text='';const order=new OutputOrder(data=>text+=data);
  order.accept({sessionId:'s',sequence:2,data:'世界'});order.accept({sessionId:'s',sequence:3,data:'!'});
  order.replay({sessionId:'s',sequence:2,data:'你好世界',truncated:false});
  order.accept({sessionId:'s',sequence:3,data:'!'});order.accept({sessionId:'s',sequence:4,data:'\r\n'});
  assert.equal(text,'你好世界!\r\n');assert.equal(order.sequence,4);
});
test('failed replay still releases buffered future output in order',()=>{
  let text='';const order=new OutputOrder(data=>text+=data);order.accept({sessionId:'s',sequence:2,data:'B'});order.accept({sessionId:'s',sequence:1,data:'A'});order.failed();order.accept({sessionId:'s',sequence:3,data:'C'});assert.equal(text,'ABC');
});
