// Net Watcher website: release info for the download buttons, and the interactive app preview.
// All data in the preview is fictional sample data.

(() => {
  "use strict";

  // ------------------------------------------------------------------
  // Downloads. On the live site they come from the latest GitHub Release
  // (repo named in <meta name="nw-repo">), so publishing a release updates
  // the page by itself. Locally, downloads/release.json from build.ps1 is used.
  // ------------------------------------------------------------------
  const fmtSize = (b) => (b / 1048576).toFixed(1) + " MB";
  const kindOf = (name) => (/\.msi$/i.test(name) ? "installer" : /\.exe$/i.test(name) ? "portable" : null);

  async function fromGitHub() {
    const repo = document.querySelector('meta[name="nw-repo"]')?.content;
    if (!repo || !/^[\w.-]+\/[\w.-]+$/.test(repo)) return null;
    const res = await fetch(`https://api.github.com/repos/${repo}/releases/latest`, { headers: { Accept: "application/vnd.github+json" } });
    if (!res.ok) return null;
    const r = await res.json();
    const files = (r.assets || [])
      .map((a) => ({
        name: a.name,
        kind: kindOf(a.name),
        bytes: a.size,
        sha256: (a.digest || "").replace(/^sha256:/, ""),
        url: a.browser_download_url,
      }))
      .filter((f) => f.kind);
    return { version: String(r.tag_name || "").replace(/^v/i, ""), date: (r.published_at || "").slice(0, 10), files };
  }

  async function fromLocalBuild() {
    const res = await fetch("downloads/release.json", { cache: "no-store" });
    if (!res.ok) return null;
    const r = await res.json();
    r.files?.forEach((f) => (f.url = "downloads/" + encodeURIComponent(f.name)));
    return r;
  }

  async function loadRelease() {
    let release = null;
    try { release = await fromGitHub(); } catch { /* offline or rate-limited */ }
    if (!release?.files?.length) {
      try { release = await fromLocalBuild(); } catch { /* not built yet */ }
    }

    if (!release || !Array.isArray(release.files)) {
      document.querySelectorAll("[data-download]").forEach((a) => {
        if (a.closest(".dl-card")) a.setAttribute("aria-disabled", "true");
        else a.href = "#download";
      });
      document.getElementById("dl-missing").hidden = false;
      return;
    }

    const byKind = Object.fromEntries(release.files.map((f) => [f.kind, f]));
    document.querySelectorAll("[data-download]").forEach((a) => {
      const f = byKind[a.dataset.download];
      if (!f) return;
      a.href = f.url;
    });

    document.querySelectorAll("[data-file-meta]").forEach((dl) => {
      const f = byKind[dl.dataset.fileMeta];
      if (!f) return;
      dl.innerHTML = `
        <dt>File</dt><dd>${f.name}</dd>
        <dt>Size</dt><dd>${fmtSize(f.bytes)}</dd>
        ${f.sha256 ? `<dt>SHA-256</dt><dd class="hash"><code title="${f.sha256}">${f.sha256}</code><button class="copy" type="button" data-copy="${f.sha256}">Copy</button></dd>` : ""}`;
    });

    document.querySelectorAll("[data-version]").forEach((el) => (el.textContent = "v" + release.version));
    const installer = byKind.installer;
    const meta = document.querySelector("[data-release-meta]");
    if (meta) meta.textContent = `Version ${release.version}${installer ? " · " + fmtSize(installer.bytes) : ""} · Free · No admin rights needed · Data stays on your PC`;
    const line = document.querySelector("[data-release-line]");
    if (line) line.textContent = `Version ${release.version}, released ${release.date}. Free for Windows 10 and 11 (64-bit). Nothing else to install.`;
  }

  document.addEventListener("click", async (e) => {
    const btn = e.target.closest("[data-copy]");
    if (!btn) return;
    try {
      await navigator.clipboard.writeText(btn.dataset.copy);
      btn.textContent = "Copied";
      setTimeout(() => (btn.textContent = "Copy"), 1500);
    } catch { /* clipboard blocked */ }
  });

  // ------------------------------------------------------------------
  // Sample data for the preview
  // ------------------------------------------------------------------
  const devices = [
    { ip: 1,  name: "Office router",        type: "Router / gateway",  vendor: "TP-Link",              mac: "50:C7:BF:1A:22:01", owner: "IT",        known: true,  infra: true },
    { ip: 10, name: "Reception PC",         type: "Windows PC",        vendor: "Dell Inc.",            mac: "B8:CA:3A:5E:10:7D", owner: "Reception", known: true },
    { ip: 11, name: "Finance laptop",       type: "Computer",          vendor: "Lenovo",               mac: "54:E1:AD:33:9C:12", owner: "Carla",     known: true },
    { ip: 12, name: "Meeting room TV",      type: "TV / media",        vendor: "LG Electronics",       mac: "A8:23:FE:61:0B:4E", owner: "Office",    known: true },
    { ip: 14, name: "IT workstation",       type: "This computer",     vendor: "ASUSTek Computer",     mac: "2C:F0:5D:7E:44:90", owner: "IT",        known: true,  infra: true },
    { ip: 20, name: "HP LaserJet",          type: "Printer",           vendor: "HP Inc.",              mac: "9C:8E:99:4B:21:F3", owner: "Office",    known: true,  critical: true, reserved: true },
    { ip: 30, name: "Camera recorder",      type: "Camera / DVR",      vendor: "Hikvision",            mac: "44:19:B6:0C:7A:58", owner: "Security",  known: true,  critical: true, reserved: true },
    { ip: 42, name: "Ana's iPhone",         type: "iPhone / iPad",     vendor: "Apple, Inc.",          mac: "F4:0F:24:9D:3B:C2", owner: "Ana",       known: true },
    { ip: 57, name: "Private MAC device",   type: "Phone or laptop",   vendor: "Private MAC",          mac: "DA:4E:91:07:5C:2B", owner: "",          known: false },
    { ip: 63, name: "Galaxy-A54",           type: "Phone / tablet",    vendor: "Samsung Electronics",  mac: "8C:79:F5:E2:60:1D", owner: "",          known: false },
    { ip: 88, name: "Warehouse tablet",     type: "Phone / tablet",    vendor: "Samsung Electronics",  mac: "10:3B:59:A8:7F:05", owner: "Warehouse", known: true,  offline: true },
  ];
  const reservedOnly = [{ ip: 25, name: "Reserved for new printer" }];

  function renderDevices() {
    const tbody = document.getElementById("device-rows");
    const rows = [...devices].sort((a, b) => (a.offline - b.offline) || a.ip - b.ip);
    tbody.innerHTML = rows.map((d) => `
      <tr class="${d.known ? "" : "unknown"} ${d.offline ? "offline" : ""}">
        <td><i class="dot ${d.offline ? "" : "on"}" title="${d.offline ? "Offline" : "Online"}"></i></td>
        <td class="star" title="${d.critical ? "Critical device" : ""}">${d.critical ? "★" : ""}</td>
        <td>192.168.0.${d.ip}</td>
        <td class="name">${d.name}</td>
        <td>${d.type}</td>
        <td>${d.vendor}</td>
        <td class="mac">${d.mac}</td>
        <td>${d.owner}</td>
        <td>${d.known ? '<span class="tick" title="Approved">✔</span>' : '<span class="cross" title="Not approved">✖</span>'}</td>
      </tr>`).join("");

    const online = devices.filter((d) => !d.offline);
    document.getElementById("st-online").textContent = online.length;
    document.getElementById("st-unknown").textContent = online.filter((d) => !d.known).length;
    document.getElementById("st-known").textContent = devices.filter((d) => d.known).length;
    document.getElementById("st-total").textContent = devices.length;
  }

  function renderIpMap() {
    const map = document.getElementById("ipmap");
    const tip = document.getElementById("ip-tip");
    const byIp = new Map(devices.filter((d) => !d.offline).map((d) => [d.ip, d]));
    const reserved = new Map(reservedOnly.map((r) => [r.ip, r]));
    const frag = document.createDocumentFragment();
    for (let i = 1; i <= 254; i++) {
      const d = byIp.get(i);
      const r = reserved.get(i);
      const cls = d ? (d.infra ? "infra" : d.known ? "known" : "unknown") : r ? "reserved" : "free";
      const label = d ? `${d.name} · ${d.vendor} · ${d.mac}${d.known ? "" : " · NOT APPROVED"}`
        : r ? r.name : "Free";
      const el = document.createElement("div");
      el.className = "tile " + cls;
      el.textContent = i;
      el.title = `192.168.0.${i} — ${label}`;
      el.addEventListener("mouseenter", () => (tip.textContent = `192.168.0.${i}: ${label}`));
      frag.appendChild(el);
    }
    map.replaceChildren(frag);
  }

  // ------------------------------------------------------------------
  // Response-time chart (same encoding as the app: blue internet, orange router,
  // red shading = no internet reply; one ms axis; hover shows exact values)
  // ------------------------------------------------------------------
  function renderChart() {
    const host = document.getElementById("chart");
    const W = 900, H = 220, L = 48, R = 112, T = 12, B = 26;
    const pw = W - L - R, ph = H - T - B;
    const now = new Date();
    now.setMinutes(0, 0, 0);
    const start = new Date(now.getTime() - 24 * 3600e3);

    // Deterministic pseudo-random so the sample looks the same on every visit.
    let seed = 7;
    const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647);
    const samples = [];
    for (let m = 0; m <= 24 * 60; m += 10) {
      const t = new Date(start.getTime() + m * 60e3);
      const h = t.getHours() + t.getMinutes() / 60;
      const busy = h > 8.5 && h < 18 ? 6 : 0; // a bit slower during office hours
      samples.push({ t, inet: Math.round(8 + busy + rnd() * 6 + (rnd() > 0.97 ? 25 : 0)), router: Math.round(1 + rnd() * 2) });
    }
    // One 11-minute outage roughly 10 hours ago.
    const outIdx = samples.length - 60;
    samples[outIdx].inet = null;
    samples[outIdx + 1].inet = null;

    const yMax = 50;
    const x = (t) => L + ((t - start) / (24 * 3600e3)) * pw;
    const y = (v) => T + ph - (Math.min(v, yMax) / yMax) * ph;
    const path = (key) => {
      let d = "", open = false;
      for (const s of samples) {
        if (s[key] == null) { open = false; continue; }
        d += (open ? "L" : "M") + x(s.t).toFixed(1) + " " + y(s[key]).toFixed(1);
        open = true;
      }
      return d;
    };
    const timeFmt = new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit" });

    let grid = "";
    for (let i = 0; i <= 4; i++) {
      const v = (yMax * i) / 4, yy = y(v);
      grid += `<line x1="${L}" x2="${L + pw}" y1="${yy}" y2="${yy}" stroke="#e8ebee"/>`;
      grid += `<text x="${L - 8}" y="${yy + 4}" text-anchor="end" font-size="11" fill="#6b7785">${v} ms</text>`;
    }
    for (let hh = 0; hh <= 24; hh += 4) {
      const t = new Date(start.getTime() + hh * 3600e3);
      grid += `<text x="${x(t)}" y="${H - 8}" text-anchor="middle" font-size="11" fill="#6b7785">${timeFmt.format(t)}</text>`;
    }
    const o0 = samples[outIdx].t, o1 = samples[outIdx + 2].t;
    // Keep the outage table and tile in step with what the chart shows.
    const mins = Math.round((o1 - o0) / 60e3);
    const day = o0.toDateString() === new Date().toDateString() ? "Today" : "Yesterday";
    document.getElementById("outage-start").textContent = `${day} ${timeFmt.format(o0)}`;
    document.getElementById("outage-duration").textContent = `${mins}m 00s`;
    document.getElementById("outage-tile").textContent = `1 · ${mins}m 00s`;
    const outage = `<rect x="${x(o0)}" y="${T}" width="${x(o1) - x(o0)}" height="${ph}" fill="rgba(208,59,59,.18)"/>
                    <rect x="${x(o0)}" y="${T + ph - 3}" width="${x(o1) - x(o0)}" height="3" fill="#d03b3b"/>`;
    const last = samples[samples.length - 1];
    const labels = `
      <circle cx="${L + pw + 10}" cy="${y(last.inet)}" r="4" fill="#2a78d6"/><text x="${L + pw + 18}" y="${y(last.inet) + 4}" font-size="11" fill="#1f2933">Internet ${last.inet} ms</text>
      <circle cx="${L + pw + 10}" cy="${y(last.router)}" r="4" fill="#eb6834"/><text x="${L + pw + 18}" y="${y(last.router) + 4}" font-size="11" fill="#1f2933">Router ${last.router} ms</text>`;

    host.innerHTML = `
      <svg viewBox="0 0 ${W} ${H}" role="img" aria-label="Response time over the last 24 hours: internet around 10 to 20 ms, router around 2 ms, one 11-minute internet outage.">
        ${grid}${outage}
        <path d="${path("router")}" fill="none" stroke="#eb6834" stroke-width="2" stroke-linejoin="round" vector-effect="non-scaling-stroke"/>
        <path d="${path("inet")}" fill="none" stroke="#2a78d6" stroke-width="2" stroke-linejoin="round" vector-effect="non-scaling-stroke"/>
        ${labels}
        <line id="cross" y1="${T}" y2="${T + ph}" stroke="#9aa4ae" visibility="hidden" vector-effect="non-scaling-stroke"/>
        <rect x="${L}" y="${T}" width="${pw}" height="${ph}" fill="transparent" id="hit"/>
      </svg>
      <div class="chart-tip" hidden></div>`;

    const svg = host.querySelector("svg"), hit = host.querySelector("#hit"), cross = host.querySelector("#cross"), tip = host.querySelector(".chart-tip");
    hit.addEventListener("mousemove", (e) => {
      const r = svg.getBoundingClientRect();
      const sx = ((e.clientX - r.left) / r.width) * W;
      const t = start.getTime() + ((sx - L) / pw) * 24 * 3600e3;
      const s = samples.reduce((a, b) => (Math.abs(b.t - t) < Math.abs(a.t - t) ? b : a));
      cross.setAttribute("x1", x(s.t)); cross.setAttribute("x2", x(s.t)); cross.setAttribute("visibility", "visible");
      tip.hidden = false;
      tip.innerHTML = `<b>${timeFmt.format(s.t)}</b><br><i style="background:#2a78d6"></i>Internet: ${s.inet == null ? "no reply" : s.inet + " ms"}<br><i style="background:#eb6834"></i>Router: ${s.router} ms${s.inet == null ? '<br><i style="background:#d03b3b"></i>No internet reply' : ""}`;
      const px = e.clientX - r.left, flip = px > r.width - 180;
      tip.style.left = (flip ? px - tip.offsetWidth - 12 : px + 12) + "px";
      tip.style.top = Math.max(4, e.clientY - r.top - 30) + "px";
    });
    hit.addEventListener("mouseleave", () => { tip.hidden = true; cross.setAttribute("visibility", "hidden"); });
  }

  // ------------------------------------------------------------------
  // Tabs (keyboard accessible)
  // ------------------------------------------------------------------
  function setupTabs() {
    const tabs = [...document.querySelectorAll('[role="tab"]')];
    const select = (tab) => {
      tabs.forEach((t) => {
        const on = t === tab;
        t.setAttribute("aria-selected", on);
        t.tabIndex = on ? 0 : -1;
        document.getElementById(t.getAttribute("aria-controls")).hidden = !on;
      });
    };
    tabs.forEach((t, i) => {
      t.tabIndex = i === 0 ? 0 : -1;
      t.addEventListener("click", () => select(t));
      t.addEventListener("keydown", (e) => {
        if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
        const next = tabs[(i + (e.key === "ArrowRight" ? 1 : tabs.length - 1)) % tabs.length];
        select(next);
        next.focus();
      });
    });
  }

  // ------------------------------------------------------------------
  // Highlight the nav link for the section in view
  // ------------------------------------------------------------------
  function setupNav() {
    const links = new Map([...document.querySelectorAll(".nav nav a:not(.btn)")].map((a) => [a.getAttribute("href").slice(1), a]));
    const io = new IntersectionObserver((entries) => {
      entries.forEach((en) => {
        if (!en.isIntersecting) return;
        links.forEach((a) => a.classList.remove("active"));
        links.get(en.target.id)?.classList.add("active");
      });
    }, { rootMargin: "-45% 0px -50% 0px" });
    links.forEach((_, id) => { const s = document.getElementById(id); if (s) io.observe(s); });
  }

  renderDevices();
  renderIpMap();
  renderChart();
  setupTabs();
  setupNav();
  loadRelease();
})();
