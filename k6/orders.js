// Baseline traffic for the Orders API: create -> read -> list -> change status.
// A small share of requests is intentionally invalid (400) or hits unknown orders (404),
// so the HTTP panels have something besides 2xx.
//
//   k6 run k6/orders.js
//   k6 run -e BASE_URL=http://localhost:8080 -e VUS=20 -e DURATION=10m k6/orders.js
import http from 'k6/http';
import { check, sleep } from 'k6';
import { uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';
const json = { headers: { 'Content-Type': 'application/json' } };

export const options = {
  scenarios: {
    orders: {
      executor: 'constant-vus',
      vus: Number(__ENV.VUS || 10),
      duration: __ENV.DURATION || '5m',
    },
  },
  thresholds: {
    // Not a performance test - just make obvious breakage visible in the k6 summary.
    checks: ['rate>0.80'],
  },
};

const customers = ['Anna Nowak', 'Jan Kowalski', 'ACME Sp. z o.o.', 'Contoso', 'Piotr Wiśniewski', 'Ewa Zielińska'];

export default function () {
  const roll = Math.random();

  if (roll < 0.05) {
    // 400 - invalid payload
    const res = http.post(`${BASE_URL}/api/orders`, JSON.stringify({ customerName: '', totalAmount: -1 }), json);
    check(res, { 'invalid order -> 400': (r) => r.status === 400 });
  } else if (roll < 0.10) {
    // 404 - unknown order
    const res = http.get(`${BASE_URL}/api/orders/${uuidv4()}`, { tags: { name: 'GET /orders/{id}' } });
    check(res, { 'unknown order -> 404': (r) => r.status === 404 });
  } else {
    const created = http.post(
      `${BASE_URL}/api/orders`,
      JSON.stringify({
        customerName: customers[Math.floor(Math.random() * customers.length)],
        totalAmount: Math.round(Math.random() * 100000) / 100 + 1,
      }),
      json,
    );
    if (!check(created, { 'create -> 201': (r) => r.status === 201 })) return;
    const id = created.json('id');

    check(http.get(`${BASE_URL}/api/orders/${id}`, { tags: { name: 'GET /orders/{id}' } }), {
      'get -> 200': (r) => r.status === 200,
    });

    // Lifecycle: most orders get paid and completed, some are cancelled.
    const path = Math.random() < 0.2 ? ['Cancelled'] : Math.random() < 0.7 ? ['Paid', 'Completed'] : ['Paid'];
    for (const status of path) {
      const res = http.put(`${BASE_URL}/api/orders/${id}/status`, JSON.stringify({ status }), {
        ...json,
        tags: { name: 'PUT /orders/{id}/status' },
      });
      check(res, { 'status change -> 200': (r) => r.status === 200 });
    }

    if (Math.random() < 0.3) {
      check(http.get(`${BASE_URL}/api/orders?limit=20`), { 'list -> 200': (r) => r.status === 200 });
    }
  }

  sleep(Math.random() * 0.5 + 0.1);
}
