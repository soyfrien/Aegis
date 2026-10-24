(() => {
  const $ = (id) => document.getElementById(id);

  const bytes = (n) => {
    if (n == null) return '–';
    const u = ['B', 'KB', 'MB', 'GB', 'TB']; let i = 0; let v = n;
    while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
    return `${v.toFixed(i ? 1 : 0)} ${u[i]}`;
  };
  const dur = (s) => {
    if (s == null) return '–';
    s = Math.floor(s); const d = Math.floor(s / 86400), h = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60);
    return (d ? d + 'd ' : '') + (d || h ? h + 'h ' : '') + m + 'm ' + (s % 60) + 's';
  };
  const pct = (used, total) => (total ? `${bytes(used)} / ${bytes(total)} (${(100 * used / total).toFixed(0)}%)` : '–');

  function fill(dl, rows) {
    dl.textContent = '';
    for (const [k, v] of rows) {
      const dt = document.createElement('dt'); dt.textContent = k;
      const dd = document.createElement('dd'); dd.textContent = v;
      dl.append(dt, dd);
    }
  }

  function render(s) {
    fill($('server'), [
      ['Hostname', s.hostname], ['OS', s.os], ['Kernel', s.kernel], ['Architecture', s.architecture],
      ['VM uptime', dur(s.uptimeSeconds)], ['App uptime', dur(s.app.uptimeSeconds)],
    ]);
    fill($('resources'), [
      ['CPUs', String(s.cpuCount)],
      ['CPU usage', s.cpuPercent == null ? '…' : s.cpuPercent + '%'],
      ['Load (1/5/15m)', (s.loadAverage || []).join(' / ')],
      ['Memory', pct(s.memory.usedBytes, s.memory.totalBytes)],
      ['Swap', pct(s.memory.swapUsedBytes, s.memory.swapTotalBytes)],
      ['Disk ' + (s.disk ? s.disk.mount : ''), s.disk ? pct(s.disk.usedBytes, s.disk.totalBytes) : '–'],
      ['App memory', bytes(s.app.workingSetBytes)],
    ]);
    fill($('software'), Object.entries(s.versions));
    const d = s.downloads;
    fill($('downloads'), [
      ['Jobs total', d.total], ['Succeeded', d.succeeded], ['Failed', d.failed], ['Active', d.active], ['Bytes served', bytes(d.bytesServed)],
    ]);
    const ul = $('recent'); ul.textContent = '';
    if (!d.recent.length) { const li = document.createElement('li'); li.textContent = 'No downloads yet.'; ul.append(li); }
    for (const j of d.recent) {
      const li = document.createElement('li');
      const st = document.createElement('span'); st.className = j.state === 'done' ? 'ok' : 'bad'; st.textContent = j.state;
      li.append(st, ` · ${j.host} · ${bytes(j.fileSize)} · ${dur(j.durationSeconds)} · ${new Date(j.startedAt).toLocaleTimeString()}`);
      if (j.error) { const e = document.createElement('div'); e.className = 'muted'; e.textContent = j.error; li.append(e); }
      ul.append(li);
    }
    $('updated').textContent = 'Updated ' + new Date(s.serverTime).toLocaleTimeString() + ' · refreshes every 5s';
  }

  async function refresh() {
    try {
      const res = await fetch('/api/stats', { credentials: 'same-origin' });
      if (res.status === 401) { $('content').hidden = true; $('login-hint').hidden = false; return; }
      if (!res.ok) throw new Error('Request failed (' + res.status + ')');
      $('login-hint').hidden = true; $('error').hidden = true; $('content').hidden = false;
      render(await res.json());
    } catch (e) { $('error').textContent = e.message; $('error').hidden = false; }
  }

  refresh();
  setInterval(() => { if (!document.hidden) refresh(); }, 5000);
})();
