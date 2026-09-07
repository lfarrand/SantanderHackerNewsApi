import { test, expect, type Page } from '@playwright/test';

async function rows(page: Page, start: number, count: number, blazor: boolean) {
  const body = page.locator('tbody tr');
  await expect(body).toHaveCount(count);
  for (let offset = 0; offset < count; offset++) {
    const index = start + offset;
    const id = 201 + index;
    await expect(body.nth(offset).locator('td')).toHaveText([
      String(index + 1), `Browser story ${id}`, `author${id}`,
      blazor ? '2023-11-14T22:13:20+00:00' : '2023-11-14T22:13:20Z',
      String(1000 - Math.floor(index / 2) * 10), String(index + 1),
    ]);
    const link = body.nth(offset).getByRole('link');
    await expect(link).toHaveAttribute('href', `https://fixture.invalid/${id}`);
    await expect(link).toHaveAttribute('target', '_blank');
    await expect(link).toHaveAttribute('rel', /noreferrer/);
  }
}

for (const ui of ['React', 'Blazor']) {
  test(`${ui}: real fixture stories and interactive pagination`, async ({ page }, testInfo) => {
    const blazor = ui === 'Blazor';
    const url = process.env[blazor ? 'HN_BLAZOR_URL' : 'HN_WEB_URL'];
    expect(url, 'Harness must supply the running container URL').toBeTruthy();
    const errors: string[] = [];
    const requests: string[] = [];
    let received = 0;
    let acknowledged = 0;
    let connected = false;
    page.on('pageerror', error => errors.push(error.stack || error.message));
    page.on('request', request => {
      if (new URL(request.url()).pathname === '/api/best-stories') requests.push(request.url());
    });
    page.on('websocket', socket => {
      if (new URL(socket.url()).pathname !== '/_blazor') return;
      connected = true;
      socket.on('framereceived', frame => { if (Buffer.isBuffer(frame.payload)) received++; });
      socket.on('framesent', frame => { if (Buffer.isBuffer(frame.payload)) acknowledged++; });
      socket.on('close', () => { connected = false; });
    });
    try {
      const response = await page.goto(`${url}${blazor ? '/' : '/browser/deep-route'}`);
      expect(response?.status()).toBe(200);
      await expect(page.locator('.toolbar')).toContainText('25 stories');
      await expect(page.locator('thead th')).toHaveText(['#', 'Title', 'Posted by', 'Time (UTC)', 'Score', 'Comments']);
      await expect(page.getByLabel('Per page')).toHaveValue('20');
      await rows(page, 0, 20, blazor);
      if (blazor) {
        // Wait for the live circuit's binary render/ack exchange, not prerender or negotiation.
        await expect.poll(() => connected && received > 0 && acknowledged > 0).toBe(true);
      } else {
        expect(requests).toHaveLength(1);
      }
      const initialRequests = requests.length;
      const button = (name: string) => page.getByRole('button', { name, exact: true });
      async function state(current: number, size: number) {
        const pages = Math.ceil(25 / size);
        await expect(page.locator('.pager')).toContainText(`Page ${current} of ${pages}`);
        await expect(button('First')).toBeEnabled({ enabled: current !== 1 });
        await expect(button('Previous')).toBeEnabled({ enabled: current !== 1 });
        await expect(button('Next')).toBeEnabled({ enabled: current !== pages });
        await expect(button('Last')).toBeEnabled({ enabled: current !== pages });
        await rows(page, (current - 1) * size, Math.min(size, 25 - (current - 1) * size), blazor);
      }
      await state(1, 20);
      const beforeAction = received;
      await button('Next').click();
      await state(2, 20);
      if (blazor) {
        expect(connected).toBe(true);
        expect(received).toBeGreaterThan(beforeAction);
      }
      await button('Previous').click();
      await state(1, 20);
      await button('Last').click();
      await state(2, 20);
      await button('First').click();
      await state(1, 20);
      await page.getByLabel('Per page').selectOption('10');
      await state(1, 10);
      await button('Next').click();
      await state(2, 10);
      await button('Last').click();
      await state(3, 10);
      await button('Previous').click();
      await state(2, 10);
      for (const size of [50, 100, 20]) {
        await page.getByLabel('Per page').selectOption(String(size));
        await state(1, size);
      }
      if (!blazor) expect(requests).toHaveLength(initialRequests);
      expect(errors, 'Uncaught browser errors').toEqual([]);
    } finally {
      await testInfo.attach('browser-evidence', {
        body: JSON.stringify({ errors, requests, connected, received, acknowledged }, null, 2),
        contentType: 'application/json',
      });
    }
  });
}