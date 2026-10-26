import { test, expect, Page } from '@playwright/test';

const run = () => Date.now().toString(36).toUpperCase() + Math.floor(Math.random() * 1000);

async function openAccount(page: Page, code: string, capacity: number, shown: string) {
  await page.goto('/');
  await page.getByLabel('Tier code').fill(code);
  await page.getByLabel('Tickets on sale').fill(String(capacity));
  await page.getByRole('button', { name: 'Add tier' }).click();
  await expect(page.getByRole('heading', { name: `Buy tickets for ${code}` })).toBeVisible();
  await expect(page.getByTestId('selected-remaining')).toHaveText(shown);
}

async function submit(page: Page, reference: string, quantity: number) {
  await page.getByLabel('Buyer reference').fill(reference);
  await page.getByLabel('Tickets', { exact: true }).fill(String(quantity));
  await page.getByRole('button', { name: 'Buy' }).click();
}

const row = (page: Page, reference: string) => page.locator(`tr[data-reference="${reference}"]`);

test('the API refuses bad input and the page says why', async ({ page }) => {
  const id = run();
  await openAccount(page, `UI-ERR-${id}`, 5, '5');

  await submit(page, `BUYER-${id}`, 7);
  await expect(page.getByRole('alert')).toContainText('at most 6 tickets');

  await submit(page, '   ', 1);
  await expect(page.getByRole('alert')).toContainText(/reference is required/i);

  await submit(page, `BUYER-${id}`, 1);
  await expect(row(page, `BUYER-${id}`).getByTestId('status')).toHaveText('Sold', {
    timeout: 20_000,
  });
  await submit(page, `BUYER-${id}`, 1);
  await expect(page.getByRole('alert')).toContainText('already');
  await expect(page.locator('tbody tr[data-reference]')).toHaveCount(1);
});

test('a poison reference is rejected, gets no document, and the amount comes back', async ({
  page,
}) => {
  const id = run();
  await openAccount(page, `UI-POI-${id}`, 5, '5');

  await submit(page, `BUYER-POISON-${id}`, 2);
  const poisoned = row(page, `BUYER-POISON-${id}`);
  await expect(poisoned.getByTestId('status')).toHaveText('Rejected', { timeout: 20_000 });
  await expect(poisoned).toContainText('poison');
  await expect(poisoned.getByTestId('eticket')).toHaveText('—');
  await expect(poisoned.getByRole('button', { name: 'Refund' })).toHaveCount(0);
  await expect(page.getByTestId('selected-remaining')).toHaveText('5');
});

test('the last of the scarce number goes to one request and the other is told it is short', async ({
  page,
}) => {
  const id = run();
  await openAccount(page, `UI-LAST-${id}`, 1, '1');

  await submit(page, `BUYER-A-${id}`, 1);
  await submit(page, `BUYER-B-${id}`, 1);

  const statuses = page.locator('tbody tr[data-reference] [data-testid="status"]');
  await expect(statuses).toHaveCount(2);
  await expect
    .poll(async () => (await statuses.allTextContents()).map((s) => s.trim()).sort(), {
      timeout: 20_000,
    })
    .toEqual(['Sold', 'Rejected'].sort());
  await expect(page.locator('tbody')).toContainText('Short');
  await expect(page.getByTestId('selected-remaining')).toHaveText('0');
});

test('the event log shows the Kafka steps and the reversal keeps the document', async ({
  page,
}) => {
  const id = run();
  await openAccount(page, `UI-LOG-${id}`, 4, '4');

  await submit(page, `BUYER-${id}`, 2);
  const mine = row(page, `BUYER-${id}`);
  await expect(mine.getByTestId('status')).toHaveText('Sold', { timeout: 20_000 });

  await mine.getByRole('button', { name: 'Event log' }).click();
  const log = page.getByRole('region', { name: 'Kafka event log' });
  await expect(log).toContainText('tickets.reserve', { timeout: 20_000 });
  await expect(log).toContainText('tickets.held');
  await expect(log).toContainText('eticket.issued');

  await mine.getByRole('button', { name: 'Refund' }).click();
  await expect(mine.getByTestId('status')).toHaveText('Refunded');
  await expect(mine.getByRole('button', { name: 'Refund' })).toHaveCount(0);
  await expect(mine.getByTestId('eticket')).toHaveText(/ET-[0-9A-F]{12}/);
  await expect(page.getByTestId('selected-remaining')).toHaveText('4');
  await expect(log).toContainText('tickets.released', { timeout: 20_000 });
});
