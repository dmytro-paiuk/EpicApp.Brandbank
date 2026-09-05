const $ = (id) => document.getElementById(id);

// The API is deployed separately, so its address comes from configuration rather than being
// assumed to be this origin. Mirrors how the portal WASM app resolves ApiBaseUrl.
let apiBaseUrl = '';
let configError = null;

// Ports the API uses when run from this solution. Kept in code rather than in appsettings.json so
// that a localhost address can never be shipped in the deployed config - the failure that causes
// is a deployed page quietly calling a developer's machine.
const LOCAL_API = { http: 'http://localhost:5221', https: 'https://localhost:7221' };

const isLocalHost = (host) => host === 'localhost' || host === '127.0.0.1';

async function loadConfig() {
    // Running locally, the API is on a known port pair on this machine; no configuration needed.
    if (isLocalHost(location.hostname)) {
        apiBaseUrl = location.protocol === 'https:' ? LOCAL_API.https : LOCAL_API.http;
        return;
    }

    // Deployed, the address comes from appsettings.json, which the web pipeline writes.
    try {
        const response = await fetch('appsettings.json', { cache: 'no-store' });
        if (response.ok) {
            const config = await response.json();
            apiBaseUrl = (config.ApiBaseUrl || '').replace(/\/+$/, '');
        }
    } catch {
        // Handled below as a missing address.
    }

    if (!apiBaseUrl) {
        configError = 'No API address is configured for this deployment. The web pipeline should '
            + 'write ApiBaseUrl into appsettings.json from the BRANDBANK_API_BASE_URL variable.';
        return;
    }

    if (/^https?:\/\/(localhost|127\.0\.0\.1)(:|\/|$)/.test(apiBaseUrl)) {
        configError = `This page is deployed at ${location.origin} but appsettings.json points at `
            + `${apiBaseUrl}, which only exists on a developer machine. The deployed config was not `
            + 'rewritten by the pipeline.';
        return;
    }

    if (location.protocol === 'https:' && apiBaseUrl.startsWith('http://')) {
        configError = `This page is served over https, so the browser blocks calls to ${apiBaseUrl}. `
            + 'Set BRANDBANK_API_BASE_URL to the https address of the API.';
    }
}

const escapeHtml = (value) =>
    String(value ?? '').replace(/[&<>"']/g, (c) =>
        ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const formatBytes = (bytes) => {
    if (!bytes) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB'];
    const i = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
    return `${(bytes / Math.pow(1024, i)).toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
};

async function api(path, options = {}) {
    if (configError) {
        throw new Error(configError);
    }

    let response;

    try {
        response = await fetch(`${apiBaseUrl}/api/brandbank${path}`, {
            headers: { 'Content-Type': 'application/json' },
            ...options
        });
    } catch {
        // fetch only throws like this when the request never completed: the API is not running,
        // or it did not allow this origin.
        throw new Error(`Could not reach the API at ${apiBaseUrl || location.origin}. `
            + "Check that the API is running, and that this page's origin "
            + `(${location.origin}) is listed in the API's Cors:AllowedOrigins.`);
    }

    const text = await response.text();
    let payload = text;
    try { payload = text ? JSON.parse(text) : null; } catch { /* non-JSON body is shown as-is */ }

    if (!response.ok) {
        throw new Error(payload?.message || payload || `Request failed with ${response.status}`);
    }

    return payload;
}

/* ---------- tabs ---------- */

document.querySelectorAll('.tab').forEach((tab) => {
    tab.addEventListener('click', () => {
        document.querySelectorAll('.tab').forEach((t) => t.classList.remove('active'));
        document.querySelectorAll('.tabpanel').forEach((p) => p.classList.add('hidden'));
        tab.classList.add('active');
        $(`tab-${tab.dataset.tab}`).classList.remove('hidden');

        if (tab.dataset.tab === 'samples') loadSamples();
        if (tab.dataset.tab === 'images') loadImages();
    });
});

/* ---------- feeds ---------- */

let feeds = [];

async function loadFeeds() {
    try {
        feeds = await api('/feeds');
        const select = $('feed');
        select.innerHTML = feeds.map((f) => `<option value="${escapeHtml(f.name)}">${escapeHtml(f.name)}</option>`).join('');

        const configured = feeds.filter((f) => f.hasKey).length;
        const status = $('feedStatus');

        if (!feeds.length) {
            status.className = 'pill err';
            status.textContent = 'no feeds configured';
        } else if (configured === 0) {
            status.className = 'pill warn';
            status.textContent = 'no API key set';
        } else {
            status.className = 'pill ok';
            status.textContent = `${configured} of ${feeds.length} feed(s) keyed`;
        }

        showFeedKey();
        select.addEventListener('change', showFeedKey);
    } catch (err) {
        $('feedStatus').className = 'pill err';
        $('feedStatus').textContent = err.message;
    }
}

function showFeedKey() {
    const feed = feeds.find((f) => f.name === $('feed').value);
    $('feedKey').textContent = feed ? `key: ${feed.maskedKey}` : '';
}

const currentFeed = () => $('feed').value;

/* ---------- rendering ---------- */

function statusPill(outcome, statusCode) {
    const cls = outcome === 'Success' ? 'ok' : outcome === 'Empty' ? 'warn' : 'err';
    return `<span class="pill ${cls}">${escapeHtml(outcome)} &middot; HTTP ${statusCode}</span>`;
}

function imagesTable(images, sampleId) {
    if (!images.length) {
        return '<p class="empty">No image URLs in this payload.</p>';
    }

    const rows = images.map((img) => `
        <tr>
            <td>${escapeHtml(img.shotType ?? '&mdash;')}</td>
            <td>${escapeHtml(img.format ?? '&mdash;')}</td>
            <td class="url">${escapeHtml(img.path)}</td>
            <td class="url"><a href="${escapeHtml(img.url)}" target="_blank" rel="noopener">${escapeHtml(img.url)}</a></td>
        </tr>`).join('');

    return `
        <h3>Image URLs (${images.length}) &mdash; leased for 15 days</h3>
        <div class="table-scroll">
            <table>
                <thead><tr><th>Shot type</th><th>Format</th><th>JSON path</th><th>URL</th></tr></thead>
                <tbody>${rows}</tbody>
            </table>
        </div>
        <div class="actions">
            <button data-download-sample="${escapeHtml(sampleId ?? '')}">Download all ${images.length} image(s)</button>
        </div>
        <div class="download-out"></div>`;
}

function renderFetch(containerId, result) {
    const container = $(containerId);

    container.innerHTML = `
        <div class="result">
            <div class="result-head">
                ${statusPill(result.outcome, result.statusCode)}
                <span class="hint">${escapeHtml(result.endpoint)} &middot; ${result.elapsedMs} ms &middot; ${result.attempts} attempt(s)</span>
            </div>
            <p class="result-msg">${escapeHtml(result.message)}</p>
            ${result.sampleId ? `
            <div class="meta">
                <span>Products <b>${result.productCount}</b></span>
                <span>Payload <b>${formatBytes(result.payloadBytes)}</b></span>
                <span>Saved as <b>${escapeHtml(result.sampleFile)}</b></span>
                <span>SHA-256 <b>${escapeHtml((result.payloadHash ?? '').slice(0, 12))}</b></span>
            </div>` : ''}
            ${result.sampleId || result.images.length ? imagesTable(result.images, result.sampleId) : ''}
            ${result.preview ? `<h3>Payload</h3><pre>${escapeHtml(result.preview)}</pre>` : ''}
        </div>`;

    wireDownloadButtons(container);
}

function renderCallResult(containerId, result, isError = false) {
    const cls = isError ? 'err' : result.statusCode >= 200 && result.statusCode < 300 ? 'ok' : 'err';
    const body = typeof result.body === 'string' ? result.body : JSON.stringify(result.body, null, 2);

    $(containerId).innerHTML = `
        <div class="result">
            <div class="result-head">
                <span class="pill ${cls}">HTTP ${result.statusCode}</span>
                <span class="hint">${result.elapsedMs ?? 0} ms</span>
            </div>
            <p class="result-msg">${escapeHtml(result.message)}</p>
            ${body ? `<pre>${escapeHtml(body)}</pre>` : ''}
        </div>`;
}

function renderError(containerId, message) {
    $(containerId).innerHTML = `
        <div class="result">
            <div class="result-head"><span class="pill err">Error</span></div>
            <p class="result-msg">${escapeHtml(message)}</p>
        </div>`;
}

async function withBusy(button, work) {
    const original = button.textContent;
    button.disabled = true;
    button.textContent = 'Working…';
    try {
        await work();
    } finally {
        button.disabled = false;
        button.textContent = original;
    }
}

/* ---------- image downloads ---------- */

function wireDownloadButtons(scope) {
    scope.querySelectorAll('[data-download-sample]').forEach((button) => {
        button.addEventListener('click', () => withBusy(button, async () => {
            const out = button.closest('.result, .sample-detail').querySelector('.download-out');
            try {
                const results = await api('/images/download', {
                    method: 'POST',
                    body: JSON.stringify({ sampleId: button.dataset.downloadSample || null, urls: [] })
                });

                const ok = results.filter((r) => r.success);
                const failed = results.filter((r) => !r.success);

                out.innerHTML = `
                    <p class="result-msg">Downloaded ${ok.length} of ${results.length} image(s).</p>
                    ${failed.length ? `<div class="table-scroll"><table>
                        <thead><tr><th>Status</th><th>Reason</th><th>URL</th></tr></thead>
                        <tbody>${failed.map((f) => `<tr><td>${f.statusCode}</td><td>${escapeHtml(f.error)}</td><td class="url">${escapeHtml(f.url)}</td></tr>`).join('')}</tbody>
                    </table></div>` : ''}
                    <div class="grid">${ok.map((r) => `
                        <div class="card">
                            <img src="${apiBaseUrl}/api/brandbank/images/${encodeURIComponent(r.file)}" alt="${escapeHtml(r.file)}" loading="lazy"/>
                            <div class="name">${escapeHtml(r.file)}<br/>${formatBytes(r.bytes)}</div>
                        </div>`).join('')}</div>`;
            } catch (err) {
                out.innerHTML = `<p class="result-msg">${escapeHtml(err.message)}</p>`;
            }
        }));
    });
}

/* ---------- actions ---------- */

$('btnGetNext').addEventListener('click', (e) => withBusy(e.target, async () => {
    try {
        const result = await api('/getnext', {
            method: 'POST',
            body: JSON.stringify({ feed: currentFeed(), products: Number($('products').value) || 1 })
        });
        renderFetch('getnextResult', result);
    } catch (err) {
        renderError('getnextResult', err.message);
    }
}));

// The product count returned is approximate, so the only reliable stop signal is a 204.
$('btnDrain').addEventListener('click', (e) => withBusy(e.target, async () => {
    const products = Number($('products').value) || 1;
    let calls = 0;
    let totalProducts = 0;
    let last = null;

    try {
        while (calls < 50) {
            calls++;
            last = await api('/getnext', {
                method: 'POST',
                body: JSON.stringify({ feed: currentFeed(), products })
            });

            if (last.outcome !== 'Success') break;
            totalProducts += last.productCount;
        }

        renderFetch('getnextResult', last);
        $('getnextResult').insertAdjacentHTML('afterbegin',
            `<p class="result-msg"><b>Drain finished:</b> ${calls} call(s), ${totalProducts} product(s) collected.
             ${calls >= 50 ? 'Stopped at the 50 call safety limit.' : ''}</p>`);
    } catch (err) {
        renderError('getnextResult', err.message);
    }
}));

$('btnGetLast').addEventListener('click', (e) => withBusy(e.target, async () => {
    try {
        renderFetch('getlastResult', await api('/getlast', {
            method: 'POST',
            body: JSON.stringify({ feed: currentFeed() })
        }));
    } catch (err) {
        renderError('getlastResult', err.message);
    }
}));

$('btnResend').addEventListener('click', (e) => withBusy(e.target, async () => {
    const mode = $('resendMode').value;
    const items = $('resendValues').value
        .split('\n')
        .map((v) => v.trim())
        .filter(Boolean)
        .map((v) => (mode === 'pvid' ? { pvid: v } : { gtin: v }));

    if (!items.length) {
        renderError('resendResult', 'Enter at least one value.');
        return;
    }

    try {
        renderCallResult('resendResult', await api('/resend', {
            method: 'POST',
            body: JSON.stringify({ feed: currentFeed(), items })
        }));
    } catch (err) {
        renderError('resendResult', err.message);
    }
}));

$('btnCoverageSample').addEventListener('click', (e) => withBusy(e.target, async () => {
    const response = await fetch(`${apiBaseUrl}/api/brandbank/coverage/sample`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: 'null'
    });
    $('coverageJson').value = await response.text();
}));

$('btnCoverage').addEventListener('click', (e) => withBusy(e.target, async () => {
    try {
        renderCallResult('coverageResult', await api('/coverage', {
            method: 'POST',
            body: JSON.stringify({ feed: currentFeed(), coverageJson: $('coverageJson').value })
        }));
    } catch (err) {
        renderError('coverageResult', err.message);
    }
}));

/* ---------- samples ---------- */

async function loadSamples() {
    const container = $('samplesList');
    $('sampleViewer').innerHTML = '';

    try {
        const samples = await api('/samples');

        if (!samples.length) {
            container.innerHTML = '<p class="empty">No payloads captured yet. Run GetNext first.</p>';
            return;
        }

        container.innerHTML = `
            <div class="table-scroll">
                <table>
                    <thead><tr><th>Captured (UTC)</th><th>Feed</th><th>Endpoint</th><th>Products</th><th>Size</th><th></th></tr></thead>
                    <tbody>${samples.map((s) => `
                        <tr>
                            <td>${escapeHtml(new Date(s.capturedUtc).toISOString().replace('T', ' ').slice(0, 19))}</td>
                            <td>${escapeHtml(s.feed)}</td>
                            <td>${escapeHtml(s.endpoint)}</td>
                            <td>${s.productCount}</td>
                            <td>${formatBytes(s.bytes)}</td>
                            <td><button data-sample="${escapeHtml(s.id)}">View</button></td>
                        </tr>`).join('')}</tbody>
                </table>
            </div>`;

        container.querySelectorAll('[data-sample]').forEach((button) => {
            button.addEventListener('click', () => withBusy(button, () => viewSample(button.dataset.sample)));
        });
    } catch (err) {
        container.innerHTML = `<p class="empty">${escapeHtml(err.message)}</p>`;
    }
}

async function viewSample(id) {
    const [body, images] = await Promise.all([
        fetch(`${apiBaseUrl}/api/brandbank/samples/${encodeURIComponent(id)}`).then((r) => r.text()),
        api(`/samples/${encodeURIComponent(id)}/images`)
    ]);

    let formatted = body;
    try { formatted = JSON.stringify(JSON.parse(body), null, 2); } catch { /* show raw */ }

    const viewer = $('sampleViewer');
    viewer.innerHTML = `
        <div class="result sample-detail">
            <h3>${escapeHtml(id)}</h3>
            ${imagesTable(images, id)}
            <h3>Payload</h3>
            <pre>${escapeHtml(formatted.length > 200000 ? `${formatted.slice(0, 200000)}\n\n... truncated for display.` : formatted)}</pre>
        </div>`;

    wireDownloadButtons(viewer);
}

$('btnRefreshSamples').addEventListener('click', (e) => withBusy(e.target, loadSamples));

/* ---------- images ---------- */

async function loadImages() {
    const container = $('imagesGrid');

    try {
        const images = await api('/images');

        container.innerHTML = images.length
            ? `<div class="grid">${images.map((i) => `
                <div class="card">
                    <img src="${apiBaseUrl}/api/brandbank/images/${encodeURIComponent(i.file)}" alt="${escapeHtml(i.file)}" loading="lazy"/>
                    <div class="name">${escapeHtml(i.file)}<br/>${formatBytes(i.bytes)}</div>
                </div>`).join('')}</div>`
            : '<p class="empty">No images downloaded yet.</p>';
    } catch (err) {
        container.innerHTML = `<p class="empty">${escapeHtml(err.message)}</p>`;
    }
}

$('btnRefreshImages').addEventListener('click', (e) => withBusy(e.target, loadImages));

loadConfig().then(loadFeeds);
