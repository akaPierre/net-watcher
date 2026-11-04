// Tiny static server to preview the website locally (no dependencies):
//   node website/serve.js        -> http://localhost:8080
// For real hosting, upload the website folder (including downloads/) to any static host.
const http = require("http");
const fs = require("fs");
const path = require("path");

const root = __dirname;
const port = Number(process.env.PORT) || 8080;
const types = {
  ".html": "text/html; charset=utf-8", ".css": "text/css; charset=utf-8", ".js": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8", ".png": "image/png", ".ico": "image/x-icon", ".svg": "image/svg+xml",
  ".msi": "application/x-msi", ".exe": "application/vnd.microsoft.portable-executable",
};

http.createServer((req, res) => {
  let rel = decodeURIComponent(new URL(req.url, "http://x").pathname);
  if (rel.endsWith("/")) rel += "index.html";
  const file = path.normalize(path.join(root, rel));
  if (!file.startsWith(root + path.sep) || path.basename(file) === "serve.js") {
    res.writeHead(403).end();
    return;
  }
  fs.stat(file, (err, st) => {
    if (err || !st.isFile()) { res.writeHead(404, { "Content-Type": "text/plain" }).end("Not found"); return; }
    const ext = path.extname(file).toLowerCase();
    const headers = { "Content-Type": types[ext] || "application/octet-stream", "Content-Length": st.size };
    if (ext === ".msi" || ext === ".exe") headers["Content-Disposition"] = `attachment; filename="${path.basename(file)}"`;
    res.writeHead(200, headers);
    if (req.method === "HEAD") { res.end(); return; }
    fs.createReadStream(file).pipe(res);
  });
}).listen(port, () => console.log(`Net Watcher website: http://localhost:${port}`));
