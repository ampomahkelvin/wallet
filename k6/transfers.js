import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';
import { randomIntBetween } from 'https://jslib.k6.io/k6-utils/1.2.0/index.js';

const errorRate = new Rate('errors');
const transferDuration = new Trend('transfer_duration');

export const options = {
  stages: [
    { duration: '30s', target: 50 },
    { duration: '1m', target: 100 },
    { duration: '30s', target: 200 },
    { duration: '1m', target: 200 },
    { duration: '30s', target: 0 },
  ],
  thresholds: {
    http_req_duration: ['p(95)<100'],
    errors: ['rate<0.01'],
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5158';
const CLIENT_1_KEY = __ENV.CLIENT_1_KEY || 'dev-key-client-1';
const CLIENT_2_KEY = __ENV.CLIENT_2_KEY || 'dev-key-client-2';

function headers(apiKey, idempotencyKey) {
  return {
    'Content-Type': 'application/json',
    'X-Api-Key': apiKey,
    'Idempotency-Key': idempotencyKey,
  };
}

function post(url, body, apiKey, idempotencyKey) {
  return http.post(`${BASE_URL}${url}`, JSON.stringify(body), {
    headers: headers(apiKey, idempotencyKey),
  });
}

function createWallet(apiKey, label) {
  const res = post('/api/wallets', { currency: 'GHS' }, apiKey, `wallet-${label}-${Date.now()}`);
  const success = check(res, { 'wallet created': (r) => r.status === 201 });
  errorRate.add(!success);
  return res.json('id');
}

function deposit(walletId, amount, apiKey, label) {
  const res = post(`/api/wallets/${walletId}/deposits`, { amount }, apiKey,
    `deposit-${label}-${Date.now()}`);
  const success = check(res, { 'deposit successful': (r) => r.status === 201 });
  errorRate.add(!success);
}

export function setup() {
  const wallet1 = createWallet(CLIENT_1_KEY, 'client-1');
  const wallet2 = createWallet(CLIENT_2_KEY, 'client-2');

  deposit(wallet1, 1000000, CLIENT_1_KEY, 'client-1');
  deposit(wallet2, 1000000, CLIENT_2_KEY, 'client-2');

  return { wallet1, wallet2 };
}

export default function (data) {
  const fromClient1 = __ITER % 2 === 0;
  const sourceId = fromClient1 ? data.wallet1 : data.wallet2;
  const destinationId = fromClient1 ? data.wallet2 : data.wallet1;
  const sourceKey = fromClient1 ? CLIENT_1_KEY : CLIENT_2_KEY;
  const amount = randomIntBetween(10, 100);

  const start = Date.now();
  const res = post('/api/transfers', {
    sourceWalletId: sourceId,
    destinationWalletId: destinationId,
    currency: 'GHS',
    amount,
  }, sourceKey, `transfer-${__VU}-${__ITER}-${Date.now()}`);
  transferDuration.add(Date.now() - start);

  const success = check(res, { 'transfer successful': (r) => r.status === 201 });
  errorRate.add(!success);

  sleep(0.1);
}
