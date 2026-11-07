import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests/electron',
  // These scenarios launch real desktop/provider processes and reopen the app.
  // Hosted Windows runners can take longer than the default assertion budget.
  timeout: 90_000,
  expect: { timeout: 15_000 },
  workers: 1,
  reporter: 'line',
});
