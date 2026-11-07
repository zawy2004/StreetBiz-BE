#!/usr/bin/env node
// A stand-in for payOS (https://payos.vn) for demos and tests: no real money moves.
//
// payOS has no sandbox, so this speaks the same merchant API StreetBiz calls:
//   POST /v2/payment-requests            create a payment link (signed with the channel's checksum key)
//   GET  /v2/payment-requests/{id}       state of a link (orderCode or paymentLinkId)
//   POST /v2/payment-requests/{id}/cancel
//   POST /confirm-webhook                register a webhook URL (a signed sample is posted to it first)
// and delivers the webhook itself, signed exactly as payOS signs it, when you press "Khách đã trả".
//
//   node scripts/payos-fake/server.mjs            (port 5199; PAYOS_FAKE_PORT overrides)
//   then set Payments:PayOs:ApiBaseUrl=http://localhost:5199 on the API.
//
// Open http://localhost:5199/dev to see the channels and links and to pay or cancel them.
// Channel keys are fixed demo values (below); connect them in StreetBiz to try the flow.

import { createServer } from 'node:http';
import { createHmac, randomBytes, randomUUID } from 'node:crypto';

const PORT = Number(process.env.PAYOS_FAKE_PORT ?? 5199);
const BASE = process.env.PAYOS_FAKE_PUBLIC_URL ?? `http://localhost:${PORT}`;

/** Demo channels: connect these keys in StreetBiz (a ward and two households). */
const CHANNELS = [
  channel('phuong', 'Phường Hải Châu 1', 'demo-ward-client-id', 'demo-ward-api-key', 'demo-ward-checksum-key',
    '970422', 'MB Bank', '0123456789', 'UBND PHUONG HAI CHAU 1'),
  channel('ho-1', 'Bánh mì Cô Lan', 'demo-vendor1-client-id', 'demo-vendor1-api-key', 'demo-vendor1-checksum-key',
    '970436', 'Vietcombank', '0987654321', 'PHAM THI LAN'),
  channel('ho-2', 'Xe nước ép Tám Ù', 'demo-vendor2-client-id', 'demo-vendor2-api-key', 'demo-vendor2-checksum-key',
    '970415', 'VietinBank', '0911222333', 'HOANG VAN TAM'),
];

/** @type {Map<number, Link>} orderCode -> link */
const links = new Map();
let webhooksEnabled = true;
const log = [];

function channel(key, label, clientId, apiKey, checksumKey, bin, shortName, accountNumber, accountName) {
  return { key, label, clientId, apiKey, checksumKey, bin, shortName, accountNumber, accountName, webhookUrl: null };
}

// ------------------------------------------------------------------------------ signatures

/** Hex HMAC-SHA256, as payOS signs and verifies. */
const hmac = (key, text) => createHmac('sha256', key).update(text, 'utf8').digest('hex');

function sortedJson(value) {
  if (Array.isArray(value)) return value.map(sortedJson);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map((k) => [k, sortedJson(value[k])]));
  }
  return value;
}

/** payOS's webhook signing text: keys sorted, "key=value" joined by "&", null/"null"/"undefined" as empty. */
function signedText(data) {
  return Object.keys(data).sort().map((key) => {
    const value = data[key];
    const text = value === null || value === undefined || value === 'null' || value === 'undefined'
      ? ''
      : Array.isArray(value) || typeof value === 'object' ? JSON.stringify(sortedJson(value)) : String(value);
    return `${key}=${text}`;
  }).join('&');
}

// ----------------------------------------------------------------------------------- VietQR

function crc16(text) {
  let crc = 0xffff;
  for (const byte of Buffer.from(text, 'utf8')) {
    crc ^= byte << 8;
    for (let i = 0; i < 8; i += 1) crc = crc & 0x8000 ? ((crc << 1) ^ 0x1021) & 0xffff : (crc << 1) & 0xffff;
  }
  return crc.toString(16).toUpperCase().padStart(4, '0');
}

const tlv = (id, value) => `${id}${String(value.length).padStart(2, '0')}${value}`;

/** A real EMVCo VietQR payload (dynamic amount) so a banking app can read it. */
function vietQr(ch, amount, description) {
  const account = tlv('00', 'A000000727') + tlv('01', tlv('00', ch.bin) + tlv('01', ch.accountNumber)) + tlv('02', 'QRIBFTTA');
  const body = tlv('00', '01') + tlv('01', '12') + tlv('38', account) + tlv('53', '704')
    + tlv('54', String(amount)) + tlv('58', 'VN') + tlv('62', tlv('08', description));
  const withCrcTag = `${body}6304`;
  return withCrcTag + crc16(withCrcTag);
}

// ----------------------------------------------------------------------------------- helpers

const send = (res, status, body, type = 'application/json; charset=utf-8') => {
  res.writeHead(status, { 'content-type': type });
  res.end(typeof body === 'string' ? body : JSON.stringify(body));
};
const ok = (res, data) => send(res, 200, { code: '00', desc: 'success', data, signature: hmac('fake', JSON.stringify(data)) });
const fail = (res, status, code, desc) => send(res, status, { code, desc, data: null });

async function readBody(req) {
  const chunks = [];
  for await (const chunk of req) chunks.push(chunk);
  const raw = Buffer.concat(chunks).toString('utf8');
  try { return raw ? JSON.parse(raw) : {}; } catch { return null; }
}

function authenticate(req) {
  const clientId = req.headers['x-client-id'];
  const apiKey = req.headers['x-api-key'];
  return CHANNELS.find((c) => c.clientId === clientId && c.apiKey === apiKey) ?? null;
}

const statusOf = (link) => (link.status === 'PENDING' && Date.now() / 1000 > link.expiredAt ? 'EXPIRED' : link.status);

function findLink(idText, ch) {
  const byCode = links.get(Number(idText));
  const found = byCode ?? [...links.values()].find((l) => l.paymentLinkId === idText);
  return found && found.channel === ch ? found : null;
}

function describe(link) {
  const status = statusOf(link);
  return {
    id: link.paymentLinkId,
    orderCode: link.orderCode,
    amount: link.amount,
    amountPaid: link.paid,
    amountRemaining: Math.max(0, link.amount - link.paid),
    status,
    createdAt: link.createdAt,
    transactions: link.transactions,
    canceledAt: link.canceledAt ?? null,
  };
}

// ------------------------------------------------------------------------------- webhooks

function webhookBody(ch, link, reference, orderCode = link.orderCode, amount = link.amount) {
  const data = {
    orderCode,
    amount,
    description: link?.description ?? 'SAMPLE',
    accountNumber: ch.accountNumber,
    reference,
    transactionDateTime: new Date().toISOString().slice(0, 19).replace('T', ' '),
    currency: 'VND',
    paymentLinkId: link?.paymentLinkId ?? 'sample',
    code: '00',
    desc: 'Thành công',
    counterAccountBankId: '',
    counterAccountBankName: '',
    counterAccountName: 'NGUYEN VAN KHACH',
    counterAccountNumber: '',
    virtualAccountName: '',
    virtualAccountNumber: '',
  };
  return { code: '00', desc: 'success', success: true, data, signature: hmac(ch.checksumKey, signedText(data)) };
}

async function post(url, body) {
  const response = await fetch(url, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body), signal: AbortSignal.timeout(10_000) });
  return response;
}

async function deliver(ch, body, label) {
  if (!ch.webhookUrl) { log.push(`${label}: kênh ${ch.key} chưa đăng ký webhook`); return false; }
  if (!webhooksEnabled) { log.push(`${label}: webhook đang tắt (giả lập mất webhook)`); return false; }
  for (let attempt = 1; attempt <= 3; attempt += 1) {
    try {
      const response = await post(ch.webhookUrl, body);
      log.push(`${label}: webhook -> ${response.status}`);
      if (response.ok) return true;
    } catch (error) {
      log.push(`${label}: webhook lỗi ${error.message}`);
    }
  }
  return false;
}

async function markPaid(link, { duplicate = false } = {}) {
  if (!duplicate) {
    if (statusOf(link) !== 'PENDING') return { ok: false, reason: `link đang ${statusOf(link)}` };
    link.status = 'PAID';
  }
  const reference = `FT${Date.now()}${randomBytes(2).toString('hex')}`.toUpperCase();
  link.paid += link.amount;
  link.transactions.push({ reference, amount: link.amount, accountNumber: link.channel.accountNumber, description: link.description, transactionDateTime: new Date().toISOString() });
  const delivered = await deliver(link.channel, webhookBody(link.channel, link, reference), `orderCode ${link.orderCode}`);
  return { ok: true, reference, delivered };
}

// ------------------------------------------------------------------------------------ pages

const esc = (text) => String(text).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const vnd = (n) => `${Number(n).toLocaleString('vi-VN')} đ`;
const page = (title, body) => `<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>${esc(title)}</title><style>body{font:15px system-ui;margin:24px auto;max-width:760px;padding:0 16px;color:#111c2b}
table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid #ddd;padding:6px 8px;text-align:left;vertical-align:top}
button{font:inherit;padding:6px 12px;margin:2px;cursor:pointer}code{background:#eef;padding:1px 4px;border-radius:3px}
.note{background:#fff4d1;padding:8px 12px;border-radius:8px}.ok{color:#0b7f43}.bad{color:#b42318}</style>${body}`;

function payPage(link) {
  const status = statusOf(link);
  return page('Trang thanh toán payOS (giả lập)', `
<h1>Trang thanh toán payOS <small>(giả lập)</small></h1>
<p class="note">Đây là máy chủ giả lập để thử nghiệm: không có tiền thật nào được chuyển.</p>
<table>
<tr><th>Nhận tiền</th><td>${esc(link.channel.accountName)} · ${esc(link.channel.shortName)} ${esc(link.channel.accountNumber)}</td></tr>
<tr><th>Số tiền</th><td><b>${vnd(link.amount)}</b></td></tr>
<tr><th>Nội dung</th><td><code>${esc(link.description)}</code></td></tr>
<tr><th>Mã đơn (orderCode)</th><td>${link.orderCode}</td></tr>
<tr><th>Trạng thái</th><td class="${status === 'PAID' ? 'ok' : status === 'PENDING' ? '' : 'bad'}">${status}</td></tr>
</table>
${status === 'PENDING' ? `<form method="post" action="/pay/${link.paymentLinkId}/paid"><button>Khách đã trả (gửi webhook về StreetBiz)</button></form>
<form method="post" action="/pay/${link.paymentLinkId}/cancel"><button>Huỷ</button></form>` : ''}
<p><a href="${esc(link.returnUrl)}">Quay lại StreetBiz</a></p>`);
}

function devPage() {
  const rows = [...links.values()].reverse().map((l) => `<tr><td>${l.orderCode}</td><td>${esc(l.channel.label)}</td><td>${vnd(l.amount)}</td>
<td>${statusOf(l)}</td><td>${l.transactions.length}</td><td>
${statusOf(l) === 'PENDING' ? `<form method="post" action="/pay/${l.paymentLinkId}/paid" style="display:inline"><button>Đã trả</button></form>` : ''}
${statusOf(l) === 'PAID' ? `<form method="post" action="/dev/duplicate/${l.orderCode}" style="display:inline"><button>Chuyển lần 2</button></form>` : ''}
${statusOf(l) === 'PENDING' ? `<form method="post" action="/pay/${l.paymentLinkId}/cancel" style="display:inline"><button>Huỷ</button></form>` : ''}</td></tr>`).join('');
  const channels = CHANNELS.map((c) => `<tr><td>${esc(c.label)}</td><td><code>${esc(c.clientId)}</code></td><td><code>${esc(c.apiKey)}</code></td>
<td><code>${esc(c.checksumKey)}</code></td><td>${c.webhookUrl ? esc(c.webhookUrl) : '<i>chưa đăng ký</i>'}</td></tr>`).join('');
  return page('payOS giả lập', `
<h1>payOS giả lập</h1>
<p class="note">Kết nối các khoá demo bên dưới trong StreetBiz (Tài khoản nhận tiền). Webhook:
<b>${webhooksEnabled ? 'đang bật' : 'đang TẮT (thử lớp đối soát)'}</b>
<form method="post" action="/dev/webhooks/${webhooksEnabled ? 'off' : 'on'}" style="display:inline"><button>${webhooksEnabled ? 'Tắt webhook' : 'Bật webhook'}</button></form></p>
<h2>Kênh thanh toán (khoá demo)</h2>
<table><tr><th>Kênh</th><th>Client ID</th><th>API Key</th><th>Checksum Key</th><th>Webhook</th></tr>${channels}</table>
<h2>Link thanh toán</h2>
<table><tr><th>orderCode</th><th>Kênh</th><th>Số tiền</th><th>Trạng thái</th><th>Số lần trả</th><th></th></tr>${rows || '<tr><td colspan="6"><i>Chưa có</i></td></tr>'}</table>
<h2>Nhật ký</h2><pre>${esc(log.slice(-25).join('\n'))}</pre>`);
}

// -------------------------------------------------------------------------------- the server

const server = createServer(async (req, res) => {
  const url = new URL(req.url ?? '/', BASE);
  const path = url.pathname.replace(/\/+$/, '') || '/';
  try {
    // ---- payOS merchant API
    if (req.method === 'POST' && path === '/v2/payment-requests') {
      const ch = authenticate(req);
      if (!ch) return fail(res, 401, '401', 'Unauthorized');
      const body = await readBody(req);
      if (!body) return fail(res, 400, '20', 'Dữ liệu không hợp lệ');
      const { orderCode, amount, description, cancelUrl, returnUrl, signature, expiredAt } = body;
      const expected = hmac(ch.checksumKey, `amount=${amount}&cancelUrl=${cancelUrl}&description=${description}&orderCode=${orderCode}&returnUrl=${returnUrl}`);
      if (signature !== expected) return fail(res, 400, '201', 'Chữ ký không hợp lệ');
      if (!Number.isInteger(orderCode) || !Number.isInteger(amount) || amount <= 0) return fail(res, 400, '20', 'Thông tin không hợp lệ');
      if (links.has(orderCode)) return fail(res, 400, '231', 'Đơn thanh toán đã tồn tại');
      const link = {
        orderCode, amount, description: String(description).slice(0, 25), returnUrl, cancelUrl, channel: ch, status: 'PENDING', paid: 0,
        paymentLinkId: randomUUID().replaceAll('-', ''), createdAt: new Date().toISOString(),
        expiredAt: expiredAt ?? Math.floor(Date.now() / 1000) + 15 * 60, transactions: [],
      };
      links.set(orderCode, link);
      log.push(`orderCode ${orderCode}: tạo link ${vnd(amount)} cho ${ch.label}`);
      return ok(res, {
        bin: ch.bin, accountNumber: ch.accountNumber, accountName: ch.accountName, amount, description: link.description, orderCode,
        currency: 'VND', paymentLinkId: link.paymentLinkId, status: 'PENDING', checkoutUrl: `${BASE}/pay/${link.paymentLinkId}`,
        qrCode: vietQr(ch, amount, link.description),
      });
    }

    const single = /^\/v2\/payment-requests\/([^/]+)(\/cancel)?$/.exec(path);
    if (single) {
      const ch = authenticate(req);
      if (!ch) return fail(res, 401, '401', 'Unauthorized');
      const link = findLink(decodeURIComponent(single[1]), ch);
      if (!link) return fail(res, 404, '101', 'Không tìm thấy đơn thanh toán');
      if (req.method === 'GET' && !single[2]) return ok(res, describe(link));
      if (req.method === 'POST' && single[2]) {
        if (statusOf(link) === 'PENDING') { link.status = 'CANCELLED'; link.canceledAt = new Date().toISOString(); }
        return ok(res, describe(link));
      }
    }

    if (req.method === 'POST' && path === '/confirm-webhook') {
      const ch = authenticate(req);
      if (!ch) return fail(res, 401, '401', 'Unauthorized');
      const body = await readBody(req);
      if (!body?.webhookUrl) return fail(res, 400, '20', 'Thiếu webhookUrl');
      try {
        const response = await post(body.webhookUrl, webhookBody(ch, null, 'SAMPLE-REF', 123, 3000));
        if (!response.ok) return fail(res, 400, '20', `Webhook URL trả về HTTP ${response.status}`);
      } catch (error) {
        return fail(res, 400, '20', `Không gọi được webhook URL: ${error.message}`);
      }
      ch.webhookUrl = body.webhookUrl;
      log.push(`${ch.key}: đăng ký webhook ${body.webhookUrl}`);
      return ok(res, { webhookUrl: ch.webhookUrl, accountNumber: ch.accountNumber, accountName: ch.accountName, name: ch.shortName, shortName: ch.shortName });
    }

    // ---- the payer's side and the dev controls
    const payRoute = /^\/pay\/([^/]+)(?:\/(paid|cancel))?$/.exec(path);
    if (payRoute) {
      const link = [...links.values()].find((l) => l.paymentLinkId === payRoute[1]);
      if (!link) return send(res, 404, page('Không tìm thấy', '<p>Không tìm thấy link thanh toán.</p>'), 'text/html; charset=utf-8');
      if (req.method === 'POST' && payRoute[2] === 'paid') await markPaid(link);
      if (req.method === 'POST' && payRoute[2] === 'cancel' && statusOf(link) === 'PENDING') { link.status = 'CANCELLED'; link.canceledAt = new Date().toISOString(); }
      if (req.method === 'POST') { res.writeHead(303, { location: req.headers.referer?.includes('/dev') ? '/dev' : `/pay/${link.paymentLinkId}` }); return res.end(); }
      return send(res, 200, payPage(link), 'text/html; charset=utf-8');
    }
    if (req.method === 'GET' && path === '/dev') return send(res, 200, devPage(), 'text/html; charset=utf-8');
    if (req.method === 'POST' && path.startsWith('/dev/webhooks/')) {
      webhooksEnabled = path.endsWith('/on');
      res.writeHead(303, { location: '/dev' }); return res.end();
    }
    const duplicate = /^\/dev\/duplicate\/(\d+)$/.exec(path);
    if (req.method === 'POST' && duplicate) {
      const link = links.get(Number(duplicate[1]));
      if (link) await markPaid(link, { duplicate: true });
      res.writeHead(303, { location: '/dev' }); return res.end();
    }

    // ---- JSON controls for scripts: pay a link, read state, switch webhooks
    if (req.method === 'POST' && path === '/dev/pay') {
      const body = await readBody(req);
      const link = links.get(Number(body?.orderCode));
      if (!link) return send(res, 404, { error: 'unknown orderCode' });
      return send(res, 200, await markPaid(link, { duplicate: body?.duplicate === true }));
    }
    if (req.method === 'POST' && path === '/dev/webhooks') {
      const body = await readBody(req);
      webhooksEnabled = body?.enabled !== false;
      return send(res, 200, { webhooksEnabled });
    }
    if (req.method === 'GET' && path === '/dev/state') {
      return send(res, 200, {
        webhooksEnabled,
        channels: CHANNELS.map(({ key, webhookUrl }) => ({ key, webhookUrl })),
        links: [...links.values()].map((l) => ({ orderCode: l.orderCode, channel: l.channel.key, amount: l.amount, status: statusOf(l), paid: l.paid })),
        log: log.slice(-50),
      });
    }
    if (req.method === 'GET' && path === '/') { res.writeHead(303, { location: '/dev' }); return res.end(); }
    return fail(res, 404, '404', 'Not found');
  } catch (error) {
    console.error(error);
    return fail(res, 500, '500', error.message);
  }
});

server.listen(PORT, () => {
  console.log(`payOS giả lập: ${BASE}  (trang điều khiển: ${BASE}/dev)`);
  console.log('Khoá demo của các kênh được liệt kê ở trang điều khiển.');
});
