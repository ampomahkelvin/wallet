import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';
import { randomIntBetween } from 'https://jslib.k6.io/k6-utils/1.2.0/index.js';

const errorRate = new Rate('errors');
const transferDuration = new Trend('transfer_duration');
const depositDuration = new Trend('deposit_duration');

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
const CLIENT_ID = `k6-client-${__VU}`;

function headers(idempotencyKey) {
  return {
    'Content-Type': 'application/json',
    'X-Client-Id': CLIENT_ID,
    'Idempotency-Key': idempotencyKey,
  };
}

function createWallet(ownerId) {
  const res = http.post(`${BASE_URL}/api/wallets`, JSON.stringify({
    ownerId,
    currency: 'GHS',
  }), {
    headers: headers(`wallet-${ownerId}-${Date.now()}`),
  });
  const success = check(res, { 'wallet created': (r) => r.status === 201 });
  errorRate.add(!success);
  return res.json('id');
}

function deposit(walletId, amount) {
  const start = Date.now();
  const res = http.post(`${BASE_URL}/api/wallets/${walletId}/deposits`, JSON.stringify({
    amount,
  }), {
    headers: headers(`deposit-${walletId}-${amount}-${Date.now()}`),
  });
  depositDuration.add(Date.now() - start);
  const success = check(res, { 'deposit successful': (r) => r.status === 201 });
  errorRate.add(!success);
}

function transfer(sourceId, destId, amount) {
  const start = Date.now();
  const res = http.post(`${BASE_URL}/api/transfers`, JSON.stringify({
    sourceWalletId: sourceId,
    destinationWalletId: destId,
    currency: 'GHS',
    amount,
  }), {
    headers: headers(`transfer-${sourceId}-${destId}-${Date.now()}`),
  });
  transferDuration.add(Date.now() - start);
  const success = check(res, { 'transfer successful': (r) => r.status === 201 });
  errorRate.add(!success);
}

export default function () {
  const ownerSuffix = `${__VU}-${__ITER}`;
  const walletA = createWallet(`k6-user-${ownerSuffix}-a`);
  const walletB = createWallet(`k6-user-${ownerSuffix}-b`);

  deposit(walletA, 10000);
  deposit(walletB, 10000);

  for (let i = 0; i < 5; i++) {
    transfer(walletA, walletB, randomIntBetween(10, 100));
    transfer(walletB, walletA, randomIntBetween(10, 100));
  }

  sleep(0.1);
}