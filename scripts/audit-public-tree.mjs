import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(scriptDirectory, "..");
const trackedFiles = execFileSync("git", ["-C", root, "ls-files", "-z"], {
  cwd: root,
  encoding: "utf8",
})
  .split("\0")
  .filter(Boolean);

const auditFile = "scripts/audit-public-tree.mjs";
const maxFileSize = 95 * 1024 * 1024;
const failures = [];

const forbiddenPaths = [
  /^qk-proxy\//i,
  /^tvbox-proxy\//i,
  /(^|\/)ProxyHost\.cs$/i,
  /(^|\/)(AGENTS|CLAUDE)\.md$/i,
];

const forbiddenText = [
  { label: "private proxy marker", pattern: /qk-proxy|tvbox-proxy/iu },
  { label: "bundled source marker", pattern: /趣看|\bqukan\b|\bpluto\b/iu },
  { label: "known source host", pattern: /maotaizy\.cc/iu },
  { label: "legacy bundled endpoint", pattern: /127\.0\.0\.1:7654/iu },
  { label: "old update repository", pattern: /orion-lib\/OrionTV/iu },
];

const secretPatterns = [
  { label: "private key", pattern: /-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----/u },
  { label: "GitHub token", pattern: /\bgh[pousr]_[A-Za-z0-9]{20,}\b/u },
  { label: "GitHub fine-grained token", pattern: /\bgithub_pat_[A-Za-z0-9_]{20,}\b/u },
  { label: "AWS access key", pattern: /\bAKIA[0-9A-Z]{16}\b/u },
  { label: "API key", pattern: /\bsk-[A-Za-z0-9]{20,}\b/u },
];

const allowedHosts = new Set([
  "api.github.com",
  "api.openai.com",
  "docs.expo.dev",
  "docs.expo.io",
  "expo.dev",
  "generativelanguage.googleapis.com",
  "github.com",
  "icons.expo.fyi",
  "img3.doubanio.com",
  "m.douban.com",
  "movie.douban.com",
  "nodejs.org",
  "openspeech.bytedance.com",
  "raw.githubusercontent.com",
  "reactnativeunistyles.vercel.app",
  "schemas.android.com",
  "schemas.microsoft.com",
  "schemas.openxmlformats.org",
  "tamagui.dev",
  "www.nativewind.dev",
  "yarnpkg.com",
]);

const urlPattern = /\b(?:https?|wss?):\/\/[^\s"'<>()[\]{}；，。]+/giu;
const videoUrlPattern = /(?:\.m3u8?(?:[/?#]|$)|\/provide\/vod(?:[/?#]|$)|\/api\.php\/provide\/vod(?:[/?#]|$))/iu;

function isReservedHost(hostname) {
  const host = hostname.toLowerCase();
  return (
    host === "localhost" ||
    host === "127.0.0.1" ||
    host === "::1" ||
    host === "example.com" ||
    host === "example.net" ||
    host === "example.org" ||
    host.endsWith(".example.com") ||
    host.endsWith(".example.net") ||
    host.endsWith(".example.org") ||
    host.endsWith(".example") ||
    !host.includes(".") ||
    /^10\./u.test(host) ||
    /^192\.168\./u.test(host) ||
    /^172\.(?:1[6-9]|2\d|3[01])\./u.test(host)
  );
}

for (const relativePath of trackedFiles) {
  const normalizedPath = relativePath.replaceAll("\\", "/");
  const fullPath = path.join(root, relativePath);

  if (forbiddenPaths.some((pattern) => pattern.test(normalizedPath))) {
    failures.push(normalizedPath + ": forbidden public path");
  }

  const stat = fs.statSync(fullPath);
  if (stat.size > maxFileSize) {
    failures.push(normalizedPath + ": file exceeds 95 MiB");
  }

  if (normalizedPath === auditFile || stat.size > 5 * 1024 * 1024) {
    continue;
  }

  const buffer = fs.readFileSync(fullPath);
  if (buffer.includes(0)) {
    continue;
  }
  const content = buffer.toString("utf8");

  for (const check of forbiddenText) {
    if (check.pattern.test(content)) {
      failures.push(normalizedPath + ": " + check.label);
    }
  }
  for (const check of secretPatterns) {
    if (check.pattern.test(content)) {
      failures.push(normalizedPath + ": possible " + check.label);
    }
  }

  if (/^(?:yarn\.lock|package-lock\.json|pnpm-lock\.yaml)$/iu.test(normalizedPath)) {
    continue;
  }

  for (const match of content.matchAll(urlPattern)) {
    const candidate = match[0].replace(/[.,;:!?]+$/u, "");
    let parsed;
    try {
      parsed = new URL(candidate);
    } catch {
      continue;
    }

    const host = parsed.hostname.toLowerCase();
    if (candidate === "https://goo.gl/fbAQLP") {
      continue;
    }
    if (isReservedHost(host)) {
      continue;
    }
    if (videoUrlPattern.test(candidate)) {
      failures.push(normalizedPath + ": real-looking video URL " + candidate);
      continue;
    }
    if (!allowedHosts.has(host)) {
      failures.push(normalizedPath + ": unreviewed external host " + host);
    }
  }
}

if (failures.length > 0) {
  console.error("Public tree audit failed:");
  for (const failure of [...new Set(failures)].sort()) {
    console.error("- " + failure);
  }
  process.exit(1);
}

console.log("Public tree audit passed (" + trackedFiles.length + " tracked files).");
