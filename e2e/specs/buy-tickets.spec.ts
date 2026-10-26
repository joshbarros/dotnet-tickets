import { test, expect } from '@playwright/test';

test('add a tier, buy tickets, see the e-ticket, refund it', async ({ page }) => {
  const run = Date.now().toString(36).toUpperCase();
  const code = `E2E-${run}`;
  const reference = `BUYER-${run}`;

  await page.goto('/');
  await page.getByLabel('Tier code').fill(code);
  await page.getByLabel('Tickets on sale').fill('8');
  await page.getByRole('button', { name: 'Add tier' }).click();

  const remaining = page.getByTestId('selected-remaining');
  await expect(page.getByRole('heading', { name: `Buy tickets for ${code}` })).toBeVisible();
  await expect(remaining).toHaveText('8');

  await page.getByLabel('Buyer reference').fill(reference);
  await page.getByLabel('Tickets', { exact: true }).fill('3');
  await page.getByRole('button', { name: 'Buy' }).click();

  const row = page.locator(`tr[data-reference="${reference}"]`);
  await expect(row.getByTestId('status')).toHaveText('Sold', { timeout: 20_000 });
  await expect(row.getByTestId('eticket')).toHaveText(/ET-[0-9A-F]{12}/);
  await expect(remaining).toHaveText('5');

  // Nothing is kept in the browser: a fresh load reads the same state from the API.
  await page.evaluate(() => localStorage.clear());
  await page.reload();
  await page.getByRole('button', { name: new RegExp(`^${code}`) }).click();
  const reloaded = page.locator(`tr[data-reference="${reference}"]`);
  await expect(reloaded.getByTestId('status')).toHaveText('Sold');
  await expect(page.getByTestId('selected-remaining')).toHaveText('5');

  await reloaded.getByRole('button', { name: 'Refund' }).click();
  await expect(reloaded.getByTestId('status')).toHaveText('Refunded');
  await expect(page.getByTestId('selected-remaining')).toHaveText('8');
  // The issued eticket is not rolled back.
  await expect(reloaded.getByTestId('eticket')).toHaveText(/ET-[0-9A-F]{12}/);
  const orders = await page.request.get(`/api/orders?tierCode=${code}`);
  const [order] = await orders.json();
  const eticket = await page.request.get(`/api/e-tickets/${order.id}`);
  expect((await eticket.json()).status).toBe('Issued');
});
