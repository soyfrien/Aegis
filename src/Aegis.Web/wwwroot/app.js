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

  function showLogin() { show($('login'), true); show($('app'), false); }
  function showApp() { show($('login'), false); show($('app'), true); }

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
    show($('status'), false); show($('result'), false); $('expires').textContent = '';
  }

  $('dl-form').addEventListener('submit', async (ev) => {
    ev.preventDefault(); showError(''); resetJob();
    $('dl-btn').disabled = true;
    try {
      const job = await api('/jobs', { method: 'POST', body: JSON.stringify({ url: $('url').value.trim() }) });
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
})();
