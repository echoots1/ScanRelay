// Drives the admin screen in a real browser against the development harness.
//
//   dotnet run --project tests/DevHarness      (in one terminal)
//   node tests/e2e/admin-screen.mjs [folder-for-screenshots]
//
// It walks the same path IT would: first-run setup, a printer, a sync, a scan,
// a failure and re-run, the change guard, and what the Helpdesk role can see.
import { createRequire } from 'node:module';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const { chromium } = createRequire(import.meta.url)('playwright');

const app = process.env.SCANRELAY_URL ?? 'http://localhost:5080';
const control = process.env.SCANRELAY_CONTROL ?? 'http://localhost:5081';
const shots = process.argv[2];
if (shots) fs.mkdirSync(shots, { recursive: true });

const post = (path) => fetch(control + path, { method: 'POST' }).then((r) => assert.ok(r.ok, `${path} answered ${r.status}`));
const state = () => fetch(control + '/state').then((r) => r.json());
let step = 0;
const shot = async (page, name) => {
  if (shots) await page.screenshot({ path: `${shots}/${String(++step).padStart(2, '0')}-${name}.png`, fullPage: true });
};
const log = (text) => console.log('✓ ' + text);
const eventually = async (check, what, ms = 20000) => {
  const end = Date.now() + ms;
  let last;
  while (Date.now() < end) {
    try { if (await check()) return; } catch (e) { last = e; }
    await new Promise((r) => setTimeout(r, 300));
  }
  throw new Error(`Timed out waiting for: ${what}${last ? ' (' + last.message + ')' : ''}`);
};
const pickGroup = async (page, label, search, name) => {
  const field = page.locator('.field', { has: page.getByText(label, { exact: true }) });
  await field.getByRole('searchbox').fill(search);
  await field.getByRole('button', { name: 'Search Entra' }).click();
  await field.locator('li', { hasText: name }).getByRole('button', { name: 'Choose' }).click();
  await field.locator('.badge', { hasText: name }).waitFor();
};

const browser = await chromium.launch();
const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
const page = await context.newPage();
page.setDefaultTimeout(20000);
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
process.on('uncaughtException', async (e) => {
  console.error(e.message);
  if (shots) await page.screenshot({ path: `${shots}/failure.png`, fullPage: true }).catch(() => {});
  process.exit(1);
});

// ---- first run -------------------------------------------------------------
await page.goto(app + '/');
await page.getByRole('heading', { name: 'Setup', exact: true }).waitFor();
assert.match(page.url(), /\/setup$/);
await page.getByText('answers only on the server itself').waitFor();
await shot(page, 'setup-start');
log('first visit opens setup with no sign-in');

await page.getByRole('button', { name: 'Create the app registration' }).click();
await page.getByLabel('Sign-in code').waitFor();
await shot(page, 'setup-sign-in-code');
await page.getByText('The app registration was created').waitFor();
await page.getByText('Still waiting for').waitFor();
await shot(page, 'setup-waiting-for-consent');
await post('/consent');
await page.getByText('All permissions are approved.').waitFor();
log('setup created the app registration and saw the consent arrive');
await page.getByRole('button', { name: 'Next' }).click();

await pickGroup(page, 'Admin group', 'ScanRelay', 'ScanRelay Admins');
await pickGroup(page, 'Helpdesk group (optional)', 'ScanRelay', 'ScanRelay Helpdesk');
await shot(page, 'setup-groups');
await page.getByRole('button', { name: 'Next' }).click();
await pickGroup(page, 'Default sync group', 'All', 'All Staff');
await page.getByRole('button', { name: 'Next' }).click();
await page.getByLabel('Alert sender').fill('alerts@example.com');
await page.getByRole('button', { name: 'Save' }).click();
await page.getByRole('heading', { name: '5. Add the first printer' }).waitFor();
log('groups and alert mailbox chosen');

// ---- a printer ---------------------------------------------------------------
const { printer } = await state();
await page.getByRole('link', { name: 'Open the Printers page' }).click();
await page.getByRole('button', { name: 'Add a printer' }).click();
await page.getByText('No module covers this model').waitFor();
await shot(page, 'printers-installed-on-server');
await page.locator('tr', { hasText: 'Front office printer' }).getByRole('button', { name: 'Choose' }).click();
await page.getByLabel('port').fill(String(printer.port));
await page.getByLabel('port').blur();
await page.getByLabel('User name').fill(printer.adminUser);
await page.getByLabel('Password').fill(printer.adminPassword);
await shot(page, 'printer-form');
await page.getByRole('button', { name: 'Save' }).click();
await page.getByText('Saved. Test the connection').waitFor();
await page.getByRole('button', { name: 'Test connection' }).click();
await page.getByText('answered. Model: MockPrinter 1000').waitFor();
await page.getByRole('button', { name: 'Push test entry' }).click();
await page.getByText('"ScanRelay test" is now on').waitFor();
assert.deepEqual((await state()).printerEntries, ['ScanRelay test']);
log('printer added, connection tested, test entry pushed');

// ---- sync --------------------------------------------------------------------
await page.getByRole('link', { name: 'Runs' }).click();
await page.getByRole('button', { name: 'Preview a sync' }).click();
await page.getByText('This is what a sync would change').waitFor();
await page.getByText('People added (7)').waitFor();
assert.deepEqual((await state()).printerEntries, ['ScanRelay test'], 'a preview must change nothing');
await shot(page, 'runs-preview');
await page.getByRole('button', { name: 'Sync now' }).click();
await page.getByText('The sync has finished').waitFor();
await eventually(async () => (await state()).printerEntries.length === 7, 'seven people on the printer');
assert.equal((await state()).printerOther.length, 3, 'entries the service does not own were left alone');
await shot(page, 'runs-after-sync');
log('preview changed nothing; sync put 7 people on the printer and left its other entries alone');

// ---- a printer with its own group ----------------------------------------------
await page.getByRole('link', { name: 'Printers' }).click();
await page.locator('tr', { hasText: 'Front office printer' }).getByRole('button', { name: 'Edit' }).click();
await pickGroup(page, "This printer's own group (optional)", 'Front', 'Front Office');
await page.getByRole('button', { name: 'Save' }).click();
await page.getByText('Own group: Front Office').waitFor();
await page.getByRole('link', { name: 'Runs' }).click();
await page.getByRole('button', { name: 'Sync now' }).click();
await eventually(async () => (await state()).printerEntries.join() === 'Ann Example,Bob Example', 'only the printer\'s own group on it');
await page.getByRole('link', { name: 'Printers' }).click();
await page.locator('tr', { hasText: 'Front office printer' }).getByRole('button', { name: 'Edit' }).click();
await page.locator('.field', { has: page.getByText("This printer's own group (optional)", { exact: true }) }).getByRole('button', { name: 'Remove' }).click();
await page.getByRole('button', { name: 'Save' }).click();
await page.getByText('Default sync group').waitFor();
await page.getByRole('link', { name: 'Runs' }).click();
await page.getByRole('button', { name: 'Sync now' }).click();
await eventually(async () => (await state()).printerEntries.length === 7, 'the default group back on the printer');
log('a printer given its own group lists only that group, and goes back to the default group when it is removed');

// ---- a scan ------------------------------------------------------------------
await post('/scan/Ann%20Example');
await page.getByRole('link', { name: 'Jobs' }).click();
await page.getByRole('heading', { name: 'Jobs', exact: true }).waitFor();
await page.locator('tr', { hasText: 'Ann Example' }).locator('.badge.ok').waitFor({ timeout: 30000 });
assert.equal((await state()).oneDrive['Ann Example'].length, 1);
await shot(page, 'jobs-uploaded');
log('a scan reached OneDrive and shows as Uploaded');

// ---- finish setup by signing in ---------------------------------------------------
await page.goto(app + '/setup?step=finish');
await page.getByRole('link', { name: 'Sign in to finish' }).click();
await page.getByText('Setup is finished. The admin screen needs sign-in from now on.').waitFor();
await page.getByText('Alex Admin').first().waitFor();
await shot(page, 'setup-finished');
log('signing in as an admin finished setup');

const anonymous = await browser.newContext();
const anonPage = await anonymous.newPage();
await post('/sign-in-as/Sam%20Stranger');
await anonPage.goto(app + '/settings');
await anonPage.getByText('not in the group').waitFor();
await anonymous.close();
log('someone outside the groups is turned away once setup is finished');

// ---- failure and re-run ---------------------------------------------------------
await post('/scan-to-folder/not-a-person?file=lost.pdf');
await post('/graph/fail/8');
await post('/scan/Bob%20Example?file=bob.pdf');
await page.goto(app + '/jobs?status=Failed');
await page.locator('tr', { hasText: 'not-a-person' }).getByText('matches no known person').waitFor({ timeout: 30000 });
await shot(page, 'jobs-failed');
log('a scan to an unknown folder fails with a readable reason');
await page.goto(app + '/jobs');
await page.locator('tr', { hasText: 'Bob Example' }).getByText('Uploading').waitFor({ timeout: 30000 });
await shot(page, 'jobs-retrying');
log('a Graph error is retried, and the retry is visible');

// ---- change guard ----------------------------------------------------------------
await post('/graph/fail/0');
await post('/group/All%20Staff/clear');
await page.getByRole('link', { name: 'Runs' }).click();
await page.getByRole('button', { name: 'Sync now' }).click();
await page.getByText('Waiting for approval').waitFor();
await page.getByText('more than the change guard limit of 5').waitFor();
assert.equal((await state()).printerEntries.length, 7, 'a stopped run must change nothing');
await shot(page, 'runs-change-guard');
await page.getByRole('button', { name: 'Cancel' }).click();
await page.getByText('The run was cancelled').waitFor();
assert.equal((await state()).printerEntries.length, 7);
log('emptying the group is stopped by the change guard, and cancelling leaves everything in place');
await eventually(async () => (await state()).outbox.some((m) => m.subject.includes('change guard')), 'the change guard email');
log('the admins were emailed about it');

// ---- settings, people, modules, audit -------------------------------------------
await page.getByRole('link', { name: 'Settings' }).click();
await page.getByLabel('Keep processed files for, in days').fill('14');
await page.getByLabel('Keep processed files for, in days').blur();
await page.getByRole('button', { name: 'Save settings' }).click();
await page.getByText('Settings saved.').waitFor();
await page.getByLabel('Sync interval, in minutes').fill('1');
await page.getByLabel('Sync interval, in minutes').blur();
await page.getByRole('button', { name: 'Save settings' }).click();
await page.getByText('Sync interval must be between').waitFor();
await page.getByRole('button', { name: 'Run the health check' }).click();
await page.getByText('Signed in to Example Organisation').waitFor();
await page.locator('dd', { has: page.getByRole('button', { name: 'Show' }) }).getByRole('button').click();
await page.getByText('share-password').waitFor();
await shot(page, 'settings');
log('settings save, refuse a bad value, and run the health check');

const syncField = page.locator('.field', { has: page.getByText('Default sync group', { exact: true }) });
await syncField.getByRole('button', { name: 'Remove' }).click();
await syncField.getByText('Removing the sync group stops syncing').waitFor();
await syncField.getByRole('button', { name: 'Yes, remove it' }).click();
await page.getByText('The sync group was removed.').waitFor();
await page.getByRole('link', { name: 'Runs' }).click();
await page.getByText('No sync group is chosen, so syncing is stopped.').waitFor();
assert.equal((await state()).printerEntries.length, 7, 'removing the sync group must leave the printers as they are');
await page.getByRole('link', { name: 'Settings' }).click();
await pickGroup(page, 'Default sync group', 'All', 'All Staff');
await page.getByText('The sync group was changed and saved.').waitFor();
log('the sync group can be removed, which stops syncing and changes nothing, and chosen again');

await page.getByRole('link', { name: 'People' }).click();
await page.getByLabel('Search the tenant').fill('Manual');
await page.getByRole('button', { name: 'Search the tenant' }).click();
await page.locator('li', { hasText: 'Manual Person' }).getByRole('button', { name: 'Add' }).click();
await page.getByText('Added. They are put on every printer').waitFor();
await page.getByLabel('Search the tenant').fill('Nora');
await page.getByRole('button', { name: 'Search the tenant' }).click();
await page.locator('li', { hasText: 'Nora NoDrive' }).getByRole('button', { name: 'Add' }).click();
await page.getByText('has no OneDrive').waitFor();
await shot(page, 'people');
log('a person can be added by hand, and one without OneDrive is refused');

await page.getByRole('link', { name: 'Modules' }).click();
await page.locator('tr', { hasText: 'scanrelay-sample' }).getByText('Loaded').waitFor();
await page.getByText('Official').first().waitFor();
await shot(page, 'modules');
await page.getByRole('link', { name: 'Audit log' }).click();
await page.getByText('Setup finished').waitFor();
await page.getByText('Printer added').waitFor();
await page.getByText('Printer account password shown').waitFor();
await shot(page, 'audit');
await page.getByRole('link', { name: 'Dashboard' }).click();
await page.getByRole('heading', { name: 'Recent errors' }).waitFor();
await shot(page, 'dashboard');
log('modules, audit log and dashboard show what happened');

// ---- helpdesk ----------------------------------------------------------------------
await post('/sign-in-as/Harper%20Helpdesk');
const helpdesk = await browser.newContext({ viewport: { width: 390, height: 800 } });
const hp = await helpdesk.newPage();
hp.setDefaultTimeout(20000);
await hp.goto(app + '/');
await hp.getByRole('heading', { name: 'Dashboard' }).waitFor();
await hp.getByText('Harper Helpdesk').waitFor();
assert.equal(await hp.getByRole('link', { name: 'Settings' }).count(), 0);
assert.equal(await hp.getByRole('link', { name: 'Printers' }).count(), 0);
await hp.getByRole('link', { name: 'Jobs', exact: true }).click();
await hp.locator('tr', { hasText: 'not-a-person' }).getByRole('button', { name: 'Re-run' }).waitFor();
assert.equal(await hp.getByRole('link', { name: 'Open file' }).count(), 0, 'Helpdesk must not be offered scan files');
await shot(hp, 'helpdesk-jobs-phone');
await hp.goto(app + '/settings');
await hp.getByText('Your role does not allow that page.').waitFor();
await hp.goto(app + '/runs');
await hp.getByRole('heading', { name: 'Runs' }).waitFor();
assert.equal(await hp.getByRole('button', { name: 'Sync now' }).count(), 0);
log('Helpdesk sees jobs and runs, can re-run, and gets nothing else');

assert.deepEqual(errors, [], 'the pages raised script errors');
await browser.close();
console.log('\nAll admin screen checks passed.');
