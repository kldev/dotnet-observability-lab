// RabbitMQ traffic: single publishes (some slow, some failing -> dead-letter queue), emails to the
// x.emails topic exchange (random <department>.<priority>), plus periodic bursts that publish from
// many threads at once through the publisher channel pool.
//
//   k6 run k6/messages.js
//   k6 run -e BASE_URL=http://localhost:8080 -e VUS=10 -e DURATION=10m k6/messages.js
import http from 'k6/http';
import { check, sleep } from 'k6';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';
const json = { headers: { 'Content-Type': 'application/json' } };
const DURATION = __ENV.DURATION || '5m';

export const options = {
  scenarios: {
    publish: {
      executor: 'constant-vus',
      vus: Number(__ENV.VUS || 5),
      duration: DURATION,
      exec: 'publish',
    },
    emails: {
      executor: 'constant-vus',
      vus: Number(__ENV.EMAIL_VUS || 3),
      duration: DURATION,
      exec: 'email',
    },
    // Every ~20 s: 1000 messages from 32 concurrent tasks - shows channel pool waits and a queue backlog.
    bursts: {
      executor: 'constant-arrival-rate',
      rate: 3,
      timeUnit: '1m',
      duration: DURATION,
      preAllocatedVUs: 1,
      exec: 'burst',
    },
  },
  thresholds: {
    checks: ['rate>0.95'],
  },
};

export function publish() {
  const roll = Math.random();
  const body = {
    text: `k6 message ${__VU}-${__ITER}`,
    // Mostly quick, sometimes slow consumers; ~3% poison messages.
    processingMs: roll < 0.1 ? 500 + Math.floor(Math.random() * 1500) : Math.floor(Math.random() * 50),
    fail: roll > 0.97,
  };
  const res = http.post(`${BASE_URL}/api/messages`, JSON.stringify(body), json);
  check(res, { 'publish -> 202': (r) => r.status === 202 });

  if (__ITER % 20 === 0) {
    check(http.get(`${BASE_URL}/api/messages/queue`), { 'queue -> 200': (r) => r.status === 200 });
  }
  sleep(0.2 + Math.random() * 0.3);
}

const departments = ['Recruitment', 'Sales', 'Support'];

export function email() {
  const body = {
    department: departments[Math.floor(Math.random() * departments.length)],
    priority: Math.random() < 0.2 ? 'Urgent' : 'Normal',
    subject: `k6 email ${__VU}-${__ITER}`,
    // Support is the slow department - its queue builds up first.
    processingMs: Math.floor(Math.random() * 40),
    fail: Math.random() > 0.98,
  };
  if (body.department === 'Support') body.processingMs += 300;
  const res = http.post(`${BASE_URL}/api/emails`, JSON.stringify(body), json);
  check(res, { 'email -> 202': (r) => r.status === 202 });
  sleep(0.2 + Math.random() * 0.3);
}

export function burst() {
  const res = http.post(
    `${BASE_URL}/api/messages/burst`,
    JSON.stringify({ count: 1000, parallelism: 32, processingMs: 5 }),
    { ...json, timeout: '120s' },
  );
  check(res, {
    'burst -> 200': (r) => r.status === 200,
    'burst: nothing failed': (r) => r.status === 200 && r.json('failed') === 0,
  });
}
