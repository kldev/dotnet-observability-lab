// Chaos traffic: normal Orders traffic with random problems switched ON,
// plus periodic calls to every /diagnostics/problem/* endpoint.
// Random problems are switched OFF (and held memory released) again in teardown.
//
//   k6 run k6/problems.js
//   k6 run -e DURATION=10m -e ERROR_RATE=0.1 -e SLOW_RATE=0.1 k6/problems.js
import http from 'k6/http';
import { sleep } from 'k6';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';
const json = { headers: { 'Content-Type': 'application/json' } };
const DURATION = __ENV.DURATION || '5m';

export const options = {
  scenarios: {
    orders: { executor: 'constant-vus', vus: Number(__ENV.VUS || 5), duration: DURATION, exec: 'orders' },
    problems: { executor: 'constant-arrival-rate', rate: 6, timeUnit: '1m', duration: DURATION,
                preAllocatedVUs: 2, exec: 'problems' },
  },
};

const rate = (name, fallback) => Number(__ENV[name] || fallback);

export function setup() {
  const settings = {
    enabled: true,
    errorRate: rate('ERROR_RATE', 0.05),
    exceptionRate: rate('EXCEPTION_RATE', 0.02),
    slowRequestRate: rate('SLOW_RATE', 0.05),
    dbErrorRate: rate('DB_ERROR_RATE', 0.03),
    slowDbRate: rate('SLOW_DB_RATE', 0.05),
    verboseLogRate: rate('VERBOSE_LOG_RATE', 0.02),
  };
  const res = http.put(`${BASE_URL}/diagnostics/random-problems`, JSON.stringify(settings), json);
  console.log(`random problems ON: ${res.status} ${res.body}`);
}

export function teardown() {
  http.put(`${BASE_URL}/diagnostics/random-problems`, JSON.stringify({ enabled: false }), json);
  http.get(`${BASE_URL}/diagnostics/problem/memory/release`);
  console.log('random problems OFF, held memory released');
}

export function orders() {
  const created = http.post(`${BASE_URL}/api/orders`,
    JSON.stringify({ customerName: 'Chaos Customer', totalAmount: 42.5 }), json);
  if (created.status === 201) {
    const id = created.json('id');
    http.get(`${BASE_URL}/api/orders/${id}`, { tags: { name: 'GET /orders/{id}' } });
    http.put(`${BASE_URL}/api/orders/${id}/status`, JSON.stringify({ status: Math.random() < 0.3 ? 'Cancelled' : 'Completed' }),
      { ...json, tags: { name: 'PUT /orders/{id}/status' } });
  }
  sleep(0.3);
}

const PROBLEM_PATHS = [
  'slow?ms=1500', 'bad-request', 'not-found', 'error', 'exception',
  'slow-db?seconds=1.5', 'db-error', 'cpu?seconds=10', 'memory?mb=50',
];

export function problems() {
  const p = PROBLEM_PATHS[Math.floor(Math.random() * PROBLEM_PATHS.length)];
  http.get(`${BASE_URL}/diagnostics/problem/${p}`, { tags: { name: `problem ${p.split('?')[0]}` } });
}
