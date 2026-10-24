(() => {
  const $ = (id) => document.getElementById(id);
  let jobId = null, pollTimer = null, oneDrive = { configured: false, connected: false };

  const show = (el, on) => { el.hidden = !on; };
  const showError = (msg) => { $('error').textContent = msg || ''; show($('error'), !!msg); };

  async function api(path, opts = {}) {
    const res = await fetch('/api' + path, {
      credentials: 'same-origin',
      ...opts,
      headers: { 'Content-Type': 'application/json', ...(opts.headers || {}) },
    });
    let body = null;
    try { body = await res.json(); } catch { /* empty */ }
    if (res.status === 401 && path !== '/login') { showLogin(); }
    if (!res.ok) {
      const msg = res.status === 429 ? 'Too many requests, slow down for a minute.' : (body && body.error) || `Request failed (${res.status})`;
      throw new Error(msg);
    }
    return body;
  }

  // ---- Re-encode option (remembered on this device; also applies to share-sheet auto-start) ----
  const reencodeBox = $('reencode');
  const REENCODE_KEY = 'aegis.reencode';
  try {
    const saved = localStorage.getItem(REENCODE_KEY);
    if (['off', 'auto', 'always'].includes(saved)) reencodeBox.value = saved;
  } catch { /* storage blocked: default stays Off */ }
  reencodeBox.addEventListener('change', () => {
    try { localStorage.setItem(REENCODE_KEY, reencodeBox.value); } catch { /* ignore */ }
  });

  // ---- Share-sheet / deep-link handling -------------------------------------------------
  // /share?url=… on the server redirects to /?target=<link>&auto=1. We keep that target in memory (and in the URL until it is used)
  // so it survives the login form, then pre-fill the box and, with auto=1, start the download.
  const params = new URLSearchParams(location.search);
  let pending = null;
  const sharedTarget = params.get('target');
  if (sharedTarget && /^https?:\/\//i.test(sharedTarget)) pending = { target: sharedTarget, auto: params.get('auto') === '1' };
  const sharedNone = params.get('shared') === 'none';
  const next = ['/shortcut', '/stats'].includes(params.get('next')) ? params.get('next') : null; // allow-list: no open redirects

  function showLogin() {
    show($('login'), true); show($('app'), false);
    const note = $('login-note');
    if (pending) {
      let host = ''; try { host = new URL(pending.target).host; } catch { /* ignore */ }
      note.textContent = 'Unlock Aegis to download the link you shared' + (host ? ' (' + host + ')' : '') + '.';
      show(note, true);
    } else { show(note, false); }
  }

  function showApp() {
    show($('login'), false); show($('app'), true);
    if (next) { location.replace(next); return; }
    if (sharedNone) { showError('I could not find a web link in what you shared. Copy the link and paste it below.'); }
    if (pending) {
      const p = pending; pending = null;
      history.replaceState(null, '', '/');
      $('url').value = p.target;
      syncClear();
      if (p.auto) $('dl-form').requestSubmit();
    }
  }

  // Small ✕ inside the URL box: shown only while the box has text; clearing also drops any pending share pre-fill
  // (and a leftover ?target= in the address bar) so a refresh cannot put the old link back.
  const urlBox = $('url'), clearBtn = $('clear-url');
  const syncClear = () => show(clearBtn, urlBox.value.length > 0);
  urlBox.addEventListener('input', syncClear);
  clearBtn.addEventListener('click', () => {
    urlBox.value = ''; pending = null; showError('');
    if (location.search) history.replaceState(null, '', '/');
    syncClear(); urlBox.focus();
  });

  async function init() {
    try {
      const s = await api('/session');
      oneDrive = s.oneDrive;
      s.authenticated ? showApp() : showLogin();
    } catch (e) { showError(e.message); showLogin(); }
  }

  $('login-form').addEventListener('submit', async (ev) => {
    ev.preventDefault(); showError('');
    try {
      await api('/login', { method: 'POST', body: JSON.stringify({ passcode: $('passcode').value }) });
      $('passcode').value = '';
      await init();
    } catch (e) { showError(e.message); }
  });

  $('logout').addEventListener('click', async () => {
    await fetch('/api/logout', { method: 'POST', credentials: 'same-origin' });
    resetJob(); showLogin();
  });

  function resetJob() {
    clearTimeout(pollTimer); jobId = null;
    show($('status'), false); show($('result'), false); $('expires').textContent = ''; $('notice').textContent = ''; show($('notice'), false);
  }

  $('dl-form').addEventListener('submit', async (ev) => {
    ev.preventDefault(); showError(''); resetJob();
    $('dl-btn').disabled = true;
    try {
      const job = await api('/jobs', { method: 'POST', body: JSON.stringify({ url: $('url').value.trim(), reencode: reencodeBox.value }) });
      jobId = job.id;
      show($('status'), true);
      render(job);
      poll();
    } catch (e) { showError(e.message); $('dl-btn').disabled = false; }
  });

  async function poll() {
    try {
      const job = await api('/jobs/' + jobId);
      render(job);
      if (job.state === 'queued' || job.state === 'running') pollTimer = setTimeout(poll, 1500);
    } catch (e) { showError(e.message); $('dl-btn').disabled = false; }
  }

  function render(job) {
    $('job-id').textContent = 'Job ' + job.id;
    $('bar').value = job.percent;
    if (job.state === 'queued' || job.state === 'running') {
      $('status-text').textContent = `${job.message}… ${job.percent ? job.percent.toFixed(0) + '%' : ''}`;
    } else if (job.state === 'failed') {
      $('status-text').textContent = 'Failed';
      showError(job.error || 'Download failed.');
      $('dl-btn').disabled = false;
    } else if (job.state === 'done') {
      $('dl-btn').disabled = false;
      const mb = job.fileSize ? ` (${(job.fileSize / 1048576).toFixed(1)} MB)` : '';
      $('status-text').textContent = 'Ready: ' + job.fileName + mb;
      $('notice').textContent = job.notice || ''; show($('notice'), !!job.notice);
      $('save').href = `/api/jobs/${job.id}/file`;
      show($('result'), true);
      if (job.expiresAt) $('expires').textContent = 'The server deletes this file at ' + new Date(job.expiresAt).toLocaleTimeString() + '.';
    }
    renderOneDrive(job);
  }

  function renderOneDrive(job) {
    const msg = $('od-msg');
    if (job.oneDrive === 'uploading') { msg.textContent = job.oneDriveMessage; $('od-btn').disabled = true; pollSoon(); }
    else if (job.oneDrive === 'done') {
      msg.innerHTML = '';
      const a = document.createElement('a'); a.href = job.oneDriveUrl || '#'; a.textContent = 'Saved to OneDrive – open'; a.target = '_blank'; a.rel = 'noopener';
      msg.append(a); $('od-btn').disabled = false;
    } else if (job.oneDrive === 'failed') { msg.textContent = job.oneDriveMessage; $('od-btn').disabled = false; }
  }
  function pollSoon() { clearTimeout(pollTimer); pollTimer = setTimeout(poll, 2000); }

  $('od-btn').addEventListener('click', async () => {
    showError('');
    try {
      if (!oneDrive.configured) {
        $('od-msg').textContent = 'OneDrive is not set up on this server (no Microsoft app registration configured). See the README.';
        return;
      }
      if (!oneDrive.connected) { await connectOneDrive(); return; }
      const job = await api(`/jobs/${jobId}/onedrive`, { method: 'POST' });
      render(job);
    } catch (e) { $('od-msg').textContent = e.message; }
  });

  async function connectOneDrive() {
    const d = await api('/onedrive/connect', { method: 'POST' });
    const box = $('od-device'); box.textContent = '';
    const p = document.createElement('div');
    p.append('Open ');
    const a = document.createElement('a'); a.href = d.verificationUri; a.textContent = d.verificationUri; a.target = '_blank'; a.rel = 'noopener';
    p.append(a, ' and enter code ');
    const c = document.createElement('code'); c.textContent = d.userCode;
    p.append(c, '. Then tap “Save to OneDrive” again.');
    box.append(p); show(box, true);
    const wait = async () => {
      const r = await api('/onedrive/poll');
      if (r.status === 'connected') { oneDrive.connected = true; show(box, false); $('od-msg').textContent = 'OneDrive connected. Tap “Save to OneDrive”.'; }
      else if (r.status === 'pending') setTimeout(wait, Math.max(d.interval, 3) * 1000);
      else { $('od-msg').textContent = r.status.replace('error:', ''); show(box, false); }
    };
    setTimeout(wait, Math.max(d.interval, 3) * 1000);
  }

  init();

  // Installable PWA (Add to Home Screen): a minimal service worker.
  if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js').catch(() => { /* not fatal */ });
})();
