// Execute com o servidor em http://localhost:5080: node Testar.mjs
import assert from 'node:assert/strict';
const base=process.env.TEST_URL||'http://localhost:5080';
class Client {
  cookies=new Map(); csrf='';
  async request(path,method='GET',body){
    const r=await fetch(base+'/api'+path,{method,headers:{'Content-Type':'application/json','Cookie':[...this.cookies].map(([k,v])=>k+'='+v).join('; '),'X-CSRF-TOKEN':this.csrf},body:body===undefined?undefined:JSON.stringify(body)});
    for(const s of r.headers.getSetCookie()){const part=s.split(';')[0],at=part.indexOf('=');this.cookies.set(part.slice(0,at),part.slice(at+1));}
    let data;try{data=await r.json();}catch{}return {status:r.status,data};
  }
  async session(){this.csrf=(await this.request('/session')).data.csrf;}
}
const c=new Client(),admin=new Client(),other=new Client();
await c.session();
assert.equal((await c.request('/admin/overview')).status,401,'Admin exige login');
assert.equal((await c.request('/register','POST',{name:'Teste',email:`test-${Date.now()}@example.com`,password:'Teste@2026'})).status,200);
await c.session();
assert.equal((await c.request('/admin/overview')).status,403,'Cliente não pode administrar');
const products=(await c.request('/products')).data,p=products[0],v=p.variants.find(v=>v.stock>=2);
assert.ok(v,'Precisa haver estoque para executar o teste');const stock=v.stock;
assert.equal((await c.request('/cart','PUT',{variantId:v.id,quantity:stock+100})).status,400,'Rejeita quantidade excessiva');
const cartResult=await c.request('/cart','PUT',{variantId:v.id,quantity:2});assert.equal(cartResult.status,200,JSON.stringify(cartResult.data));
const response=await c.request('/checkout','POST',{address:'Rua de Teste, 123 — São Paulo, SP — 01000-000'});
assert.equal(response.status,200,JSON.stringify(response.data));const order=response.data;
assert.equal(order.total,p.price*2,'Total calculado pelo servidor');
assert.equal((await c.request('/checkout','POST',{address:'Rua de Teste, 123 — São Paulo'})).status,400,'Não duplica o pedido após limpar o carrinho');
assert.equal((await c.request('/cart')).data.length,0);
assert.equal((await c.request('/products')).data.find(x=>x.id===p.id).variants.find(x=>x.id===v.id).stock,stock-2);
await other.session();await other.request('/register','POST',{name:'Outro cliente',email:`other-${Date.now()}@example.com`,password:'Teste@2026'});await other.session();
assert.equal((await other.request('/orders')).data.length,0,'Cliente não enxerga pedido alheio');
await admin.session();assert.equal((await admin.request('/login','POST',{email:'admin@sicko.local',password:'District@2026'})).status,200);await admin.session();
assert.equal((await admin.request(`/admin/orders/${order.id}/status`,'PUT',{status:'Entregue'})).status,400,'Rejeita transição inválida');
assert.equal((await admin.request(`/admin/orders/${order.id}/status`,'PUT',{status:'Cancelado'})).status,200);
assert.equal((await admin.request('/products')).data.find(x=>x.id===p.id).variants.find(x=>x.id===v.id).stock,stock,'Cancelamento devolve estoque');
assert.equal((await admin.request(`/admin/orders/${order.id}/status`,'PUT',{status:'Cancelado'})).status,400,'Não devolve estoque duas vezes');
const badCsrf=admin.csrf;admin.csrf='invalid';assert.equal((await admin.request('/logout','POST')).status,400,'Valida CSRF');admin.csrf=badCsrf;
const overview=(await admin.request('/admin/overview')).data;assert.ok(overview.orders>=1);
console.log('PASSOU: autenticação, autorização, estoque, totais, checkout, isolamento de clientes, cancelamento e CSRF.');
console.log('O teste criou duas contas e um pedido cancelado no banco de demonstração.');
