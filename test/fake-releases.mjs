// GitHub's releases for the update test (test/service.ps1), at the paths the agent asks
// (PADDOCK_UPDATE_URL): /releases/latest, /releases and /releases/download/<tag>/<file>.
//   node test/fake-releases.mjs <port> <folder>
// <folder>/<tag>/ holds each release's files; a tag whose name has a "-" is a prerelease. The
// folder is read on every request, so the test changes the releases while this runs.
import fs from "node:fs";
import http from "node:http";
import path from "node:path";

const [port, root] = [Number(process.argv[2]), path.resolve(process.argv[3])];

const releases = () =>
  fs.readdirSync(root, { withFileTypes: true }).filter((d) => d.isDirectory()).map((d) => ({
    tag_name: d.name,
    draft: false,
    prerelease: d.name.includes("-"),
    assets: fs.readdirSync(path.join(root, d.name)).map((name) => ({ name })),
  }));

const send = (res, status, body, type = "application/json") => {
  res.writeHead(status, { "Content-Type": type });
  res.end(type === "application/json" ? JSON.stringify(body) : body);
};

http.createServer((req, res) => {
  const url = new URL(req.url, "http://x");
  console.log(`${req.method} ${url.pathname}${url.search}`);
  if (url.pathname === "/releases") return send(res, 200, releases());
  if (url.pathname === "/releases/latest") {
    const stable = releases().filter((r) => !r.prerelease);
    return stable.length ? send(res, 200, stable.at(-1)) : send(res, 404, { message: "Not Found" });
  }
  const m = /^\/releases\/download\/([^/]+)\/([^/]+)$/.exec(url.pathname);
  const file = m && path.join(root, m[1], m[2]);
  if (file && path.dirname(path.dirname(file)) === root && fs.existsSync(file)) {
    return send(res, 200, fs.readFileSync(file), "application/octet-stream");
  }
  send(res, 404, { message: "Not Found" });
}).listen(port, "127.0.0.1", () => console.log(`fake releases on ${port} from ${root}`));
