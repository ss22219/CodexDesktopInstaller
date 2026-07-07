#!/usr/bin/env node
import { execFileSync } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

const appDir = process.argv[2];
if (!appDir) {
  console.error("Usage: patch-codex-app.mjs <Codex.app/Contents or app dir>");
  process.exit(2);
}

function exists(p) {
  return fs.existsSync(p);
}

function walk(dir, predicate, result = []) {
  if (!exists(dir)) return result;
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      walk(full, predicate, result);
    } else if (predicate(full, entry.name)) {
      result.push(full);
    }
  }
  return result;
}

function updateFileText(file, oldText, newText, label) {
  if (!exists(file)) return 0;
  const text = fs.readFileSync(file, "utf8");
  if (!text.includes(oldText)) return 0;
  const updated = text.split(oldText).join(newText);
  if (updated === text) return 0;
  fs.writeFileSync(file, updated, "utf8");
  console.log(`  PATCHED: ${label} -> ${path.basename(file)}`);
  return 1;
}

function updatePackageJson() {
  const packageJsonPath = path.join(appFolder, "package.json");
  if (!exists(packageJsonPath)) return 0;
  const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, "utf8"));
  let changed = 0;
  if (packageJson.codexSparkleFeedUrl !== "") {
    packageJson.codexSparkleFeedUrl = "";
    changed = 1;
  }
  if (packageJson.codexSparklePublicKey !== "") {
    packageJson.codexSparklePublicKey = "";
    changed = 1;
  }
  if (changed) {
    fs.writeFileSync(packageJsonPath, `${JSON.stringify(packageJson, null, 2)}\n`, "utf8");
    console.log("  PATCHED: disable Sparkle update feed -> package.json");
  }
  return changed;
}

function files(globPrefix, globSuffix) {
  return walk(assetsDir, (_full, name) => name.startsWith(globPrefix) && name.endsWith(globSuffix));
}

const resolvedAppDir = path.resolve(appDir);
const resourcesDir = exists(path.join(resolvedAppDir, "resources"))
  ? path.join(resolvedAppDir, "resources")
  : resolvedAppDir.endsWith("Contents")
    ? path.join(resolvedAppDir, "Resources")
    : path.join(resolvedAppDir, "Contents", "Resources");
const asarPath = path.join(resourcesDir, "app.asar");
const appFolder = path.join(resourcesDir, "app");
const assetsDir = path.join(appFolder, "webview", "assets");

if (!exists(resourcesDir)) {
  throw new Error(`Codex resources directory not found: ${resourcesDir}`);
}

if (exists(asarPath)) {
  fs.rmSync(appFolder, { recursive: true, force: true });
  execFileSync("npx", ["--yes", "@electron/asar", "extract", asarPath, appFolder], {
    cwd: resourcesDir,
    stdio: "inherit",
  });
  console.log("  OK: extracted app.asar for patching");
}

if (!exists(assetsDir)) {
  throw new Error(`Codex webview assets not found: ${assetsDir}`);
}

let patchCount = 0;
let i18nPatchCount = 0;
let modelListPatchCount = 0;

patchCount += updatePackageJson();

for (const file of walk(appFolder, (_full, name) => name.endsWith(".js") || name.endsWith(".cjs") || name.endsWith(".mjs"))) {
  patchCount += updateFileText(
    file,
    "enableUpdater:n.i.shouldIncludeUpdater(a,process.platform,process.env)",
    "enableUpdater:!1",
    "disable app updater",
  );
  patchCount += updateFileText(
    file,
    "enableSparkle:!0",
    "enableSparkle:!1",
    "disable Sparkle UI",
  );
}

for (const file of files("read-service-tier-for-request-", ".js")) {
  patchCount += updateFileText(
    file,
    "return n===`chatgpt`?(await e.query.fetch(g,{authMethod:n,hostId:t})).requirements?.featureRequirements?.fast_mode!==!1:!1",
    "return (await e.query.fetch(g,{authMethod:n,hostId:t})).requirements?.featureRequirements?.fast_mode!==!1",
    "fast mode request gate",
  );
}

for (const file of files("use-service-tier-settings-", ".js")) {
  patchCount += updateFileText(file, "s=a?.authMethod===`chatgpt`", "s=true", "fast mode settings gate");
}

for (const file of files("use-is-plugins-enabled-", ".js")) {
  patchCount += updateFileText(
    file,
    "function R({areRequiredFeaturesEnabled:e,enabled:t,isAnyFeatureLoading:n,isComputerUseGateEnabled:r,isHostCompatiblePlatform:i,isPlatformLoading:a,windowType:o}){return t?o===`electron`?r?a?`loading`:i?n?`loading`:e?`available`:`config-requirement-disabled`:`unsupported-platform`:`statsig-disabled`:`window-type-disabled`:`disabled`}",
    "function R({areRequiredFeaturesEnabled:e,enabled:t,isAnyFeatureLoading:n,isComputerUseGateEnabled:r,isHostCompatiblePlatform:i,isPlatformLoading:a,windowType:o}){return t?`available`:`disabled`}",
    "plugins availability gate",
  );
}

for (const file of files("use-plugin-install-flow-", ".js")) {
  patchCount += updateFileText(
    file,
    "(r||n!=null&&!n.isPending&&n.error==null&&n.data==null)&&(i=`connector-unavailable`)",
    "false&&(i=`connector-unavailable`)",
    "connector availability item gate",
  );
  patchCount += updateFileText(
    file,
    "!v&&y.length>0&&ne===y.length&&(k=D?`disabled-by-admin`:`connector-unavailable`)",
    "!v&&y.length>0&&ne===y.length&&D&&(k=`disabled-by-admin`)",
    "connector availability plugin gate",
  );
}

for (const file of walk(assetsDir, (_full, name) => name.endsWith(".js"))) {
  patchCount += updateFileText(
    file,
    "function Jm({authMethod:e,email:t,plan:n}){return e===`apikey`?!0:e===`chatgpt`?Ym({email:t,plan:n}):!1}",
    "function Jm({authMethod:e,email:t,plan:n}){return e===`apikey`?!1:e===`chatgpt`?Ym({email:t,plan:n}):!1}",
    "apikey plugin gate",
  );
}

const allJsFiles = walk(assetsDir, (_full, name) => name.endsWith(".js"));

for (const file of allJsFiles.filter((file) => fs.readFileSync(file, "utf8").includes("locale_source"))) {
  i18nPatchCount += updateFileText(
    file,
    "let s=o,c=a?.get(`locale_source`,`IDE`)",
    "let s=!0,c=a?.get(`locale_source`,`IDE`)",
    "i18n message loading gate",
  );
  i18nPatchCount += updateFileText(
    file,
    "let c=s,l=o?.get(`locale_source`,`IDE`)",
    "let c=!0,l=o?.get(`locale_source`,`IDE`)",
    "i18n message loading gate",
  );
}
patchCount += i18nPatchCount;

if (i18nPatchCount === 0) {
  const alreadyPatched = allJsFiles.some((file) => {
    const text = fs.readFileSync(file, "utf8");
    return text.includes("let s=!0,c=a?.get(`locale_source`,`IDE`)")
      || text.includes("let c=!0,l=o?.get(`locale_source`,`IDE`)");
  });
  if (!alreadyPatched) {
    throw new Error("Codex i18n patch was not applied. The bundled Codex i18n provider may have changed.");
  }
}

for (const file of files("model-list-filter-", ".js")) {
  modelListPatchCount += updateFileText(
    file,
    "let c=[],l=null,u=s&&e!==`amazonBedrock`,d=o.some(e=>e.supportedReasoningEfforts.some(({reasoningEffort:e})=>e===`max`)),f=a&&o.some(e=>e.supportedReasoningEfforts.some(({reasoningEffort:e})=>e===`ultra`));return o.forEach(r=>{if(u?n.has(r.model):!r.hidden){let n=a?r.supportedReasoningEfforts:r.supportedReasoningEfforts.filter(({reasoningEffort:e})=>e!==`ultra`)",
    "let c=[],l=null,u=s&&e!==`amazonBedrock`,p=e=>String(e).endsWith(`-free`),m=p(r),d=o.some(e=>e.supportedReasoningEfforts.some(({reasoningEffort:e})=>e===`max`)),f=a&&o.some(e=>e.supportedReasoningEfforts.some(({reasoningEffort:e})=>e===`ultra`));return o.forEach(r=>{if(m?!p(r.model):p(r.model))return;if(u?n.has(r.model):!r.hidden){let n=m?[{reasoningEffort:`none`,description:`Disable Thinking`}]:a?r.supportedReasoningEfforts:r.supportedReasoningEfforts.filter(({reasoningEffort:e})=>e!==`ultra`)",
    "free model mode gate",
  );
  const fixedCatalog =
    "m&&u&&[`deepseek-v4-flash-free`,`north-mini-code-free`,`mimo-v2.5-free`,`nemotron-3-ultra-free`].forEach(e=>{!c.some(t=>t.model===e)&&c.push({model:e,name:e,displayName:e.split(`-`).filter(Boolean).map(e=>e.length<=3?e.toUpperCase():`${e[0]?.toUpperCase()??``}${e.slice(1)}`).join(` `),description:e,hidden:!1,isDefault:e===r,defaultReasoningEffort:`none`,supportedReasoningEfforts:[{reasoningEffort:`none`,description:`Disable Thinking`}]})}),l??=c.find(e=>e.model===r)??null,{models:c,defaultModel:l,hasModelSupportingMaxReasoningEffort:m?!1:d,hasModelSupportingUltraReasoningEffort:m?!1:f}";
  modelListPatchCount += updateFileText(
    file,
    "u&&n.forEach(e=>{c.some(t=>t.model===e)||c.push({model:e,name:e,displayName:e,isDefault:e===r,hidden:!1,defaultReasoningEffort:`none`,supportedReasoningEfforts:[`none`,`low`,`medium`,`high`,`xhigh`].filter(e=>i.has(e)).map(e=>({reasoningEffort:e,description:e===`none`?`Disable Thinking`:`${e} effort`}))})}),l??=c.find(e=>e.model===r)??null,{models:c,defaultModel:l,hasModelSupportingMaxReasoningEffort:d,hasModelSupportingUltraReasoningEffort:f}",
    fixedCatalog,
    "free model fixed catalog",
  );
  modelListPatchCount += updateFileText(
    file,
    "l??=c.find(e=>e.model===r)??null,{models:c,defaultModel:l,hasModelSupportingMaxReasoningEffort:d,hasModelSupportingUltraReasoningEffort:f}",
    fixedCatalog,
    "free model fixed catalog",
  );
}

for (const file of allJsFiles.filter((file) => {
  const text = fs.readFileSync(file, "utf8");
  return text.includes("hasModelSupportingMaxReasoningEffort") && text.includes("useHiddenModels");
})) {
  modelListPatchCount += updateFileText(
    file,
    "let s=[],c=null,l=o&&e!==`amazonBedrock`,u=a.some(e=>e.supportedReasoningEfforts.some(({reasoningEffort:e})=>e===`max`)),d=i&&a.some(e=>e.supportedReasoningEfforts.some(({reasoningEffort:e})=>e===`ultra`));return a.forEach(n=>{if(l?t.has(n.model):!n.hidden){let t=i?n.supportedReasoningEfforts:n.supportedReasoningEfforts.filter(({reasoningEffort:e})=>e!==`ultra`)",
    "let s=[],c=null,l=o&&e!==`amazonBedrock`,p=e=>String(e).endsWith(`-free`),m=p(n),u=a.some(e=>e.supportedReasoningEfforts.some(({reasoningEffort:e})=>e===`max`)),d=i&&a.some(e=>e.supportedReasoningEfforts.some(({reasoningEffort:e})=>e===`ultra`));return a.forEach(n=>{if(m?!p(n.model):p(n.model))return;if(l?t.has(n.model):!n.hidden){let t=m?[{reasoningEffort:`none`,description:`Disable Thinking`}]:i?n.supportedReasoningEfforts:n.supportedReasoningEfforts.filter(({reasoningEffort:e})=>e!==`ultra`)",
    "free model mode gate",
  );
  modelListPatchCount += updateFileText(
    file,
    "c??=s.find(e=>e.model===n)??null,{models:s,defaultModel:c,hasModelSupportingMaxReasoningEffort:u,hasModelSupportingUltraReasoningEffort:d}",
    "m&&[`deepseek-v4-flash-free`,`north-mini-code-free`,`mimo-v2.5-free`,`nemotron-3-ultra-free`].forEach(e=>{!s.some(t=>t.model===e)&&s.push({model:e,name:e,displayName:e.split(`-`).filter(Boolean).map(e=>e.length<=3?e.toUpperCase():`${e[0]?.toUpperCase()??``}${e.slice(1)}`).join(` `),description:e,hidden:!1,isDefault:e===n,defaultReasoningEffort:`none`,supportedReasoningEfforts:[{reasoningEffort:`none`,description:`Disable Thinking`}]})}),c??=s.find(e=>e.model===n)??null,{models:s,defaultModel:c,hasModelSupportingMaxReasoningEffort:m?!1:u,hasModelSupportingUltraReasoningEffort:m?!1:d}",
    "free model fixed catalog",
  );
}
patchCount += modelListPatchCount;

for (const file of files("model-and-reasoning-dropdown-", ".js")) {
  patchCount += updateFileText(
    file,
    "let A=k,j=ne===void 0?!1:ne,de=ie===void 0?!0:ie,M=ae===void 0?!1:ae,N=fe(a,i),P=pe(x,N),",
    "let __codexFreeModels=[`deepseek-v4-flash-free`,`north-mini-code-free`,`mimo-v2.5-free`,`nemotron-3-ultra-free`],__codexIsFree=e=>__codexFreeModels.includes(String(e)),__codexLabel=e=>String(e).split(`-`).filter(Boolean).map(e=>e.length<=3?e.toUpperCase():`${e[0]?.toUpperCase()??``}${e.slice(1)}`).join(` `),__codexFree=__codexIsFree(i)||a?.some(e=>__codexIsFree(e?.model));__codexFree&&(i=__codexIsFree(i)?i:__codexFreeModels[0],a=__codexFreeModels.map(e=>({model:e,displayName:__codexLabel(e),description:__codexLabel(e),hidden:!1,isDefault:e===i,defaultReasoningEffort:`none`,supportedReasoningEfforts:[{reasoningEffort:`none`,description:`Disable Thinking`}]})),x=`none`,S=!0,le=!0);let A=k,j=ne===void 0?!1:ne,de=__codexFree?!1:(ie===void 0?!0:ie),M=__codexFree?!1:(ae===void 0?!1:ae),N=fe(a,i),P=pe(x,N),",
    "free model dropdown",
  );
}

for (const file of files("composer-", ".js")) {
  patchCount += updateFileText(
    file,
    "c=o?.models,{modelSettings:u,setModelAndReasoningEffort:d}=ja(e),f=u.model;",
    "c=o?.models,{modelSettings:u,setModelAndReasoningEffort:d}=ja(e),f=u.model,__codexComposerFreeModels=[`deepseek-v4-flash-free`,`north-mini-code-free`,`mimo-v2.5-free`,`nemotron-3-ultra-free`],__codexComposerIsFree=e=>__codexComposerFreeModels.includes(String(e)),__codexComposerFree=__codexComposerIsFree(f)||c?.some(e=>__codexComposerIsFree(e?.model));__codexComposerFree&&(f=__codexComposerIsFree(f)?f:__codexComposerFreeModels[0],u={...u,model:f,reasoningEffort:`none`},c=__codexComposerFreeModels.map(e=>({model:e,name:e,displayName:e.split(`-`).filter(Boolean).map(e=>e.length<=3?e.toUpperCase():`${e[0]?.toUpperCase()??``}${e.slice(1)}`).join(` `),description:e,hidden:!1,isDefault:e===f,defaultReasoningEffort:`none`,supportedReasoningEfforts:[{reasoningEffort:`none`,description:`Disable Thinking`}]})));",
    "free model composer label",
  );
}

const pluginRoot = path.join(resourcesDir, "plugins", "openai-bundled", "plugins");
for (const file of walk(pluginRoot, (_full, name) => name === "SKILL.md")) {
  patchCount += updateFileText(
    file,
    "The `browser-client` module is the core entry point for browser use, and is available under `scripts/browser-client.mjs` in this plugin's root directory. ALWAYS import it using an absolute path.",
    "The `browser-client` module is the core entry point for browser use, and is available under `scripts/browser-client.mjs` in this plugin's root directory. ALWAYS import it using an absolute path. On Windows, use a `file:///C:/.../browser-client.mjs` URL or a forward-slash absolute path in the dynamic import string; raw backslashes are not valid JavaScript import specifiers.",
    "browser client import guidance",
  );
}

if (modelListPatchCount === 0) {
  const alreadyPatched = allJsFiles.some((file) =>
    fs.readFileSync(file, "utf8").includes("p=e=>String(e).endsWith(`-free`)"),
  );
  if (!alreadyPatched) {
    throw new Error("Codex model list patch was not applied. The bundled Codex model selector may have changed.");
  }
}

if (patchCount === 0 && !exists(path.join(resourcesDir, "codex-installer-patch.txt"))) {
  throw new Error("No Codex app patches were applied. The bundled Codex version may have changed.");
}

const packageJsonPath = path.join(appFolder, "package.json");
if (exists(packageJsonPath)) {
  const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, "utf8"));
  if (packageJson.codexSparkleFeedUrl || packageJson.codexSparklePublicKey) {
    throw new Error("Codex auto-update feed was not disabled.");
  }
}
for (const file of walk(appFolder, (_full, name) => name.endsWith(".js") || name.endsWith(".cjs") || name.endsWith(".mjs"))) {
  const text = fs.readFileSync(file, "utf8");
  if (text.includes("enableUpdater:n.i.shouldIncludeUpdater(a,process.platform,process.env)") || text.includes("enableSparkle:!0")) {
    throw new Error(`Codex updater patch was not applied: ${file}`);
  }
}

fs.writeFileSync(path.join(appFolder, "codex-installer-patch.txt"), "Codex API mode fast/plugins/i18n/updater patch applied.", "utf8");
fs.rmSync(asarPath, { force: true });
execFileSync("npx", ["--yes", "@electron/asar", "pack", appFolder, asarPath], {
  cwd: resourcesDir,
  stdio: "inherit",
});
const infoPlist = path.join(path.dirname(resourcesDir), "Info.plist");
if (exists(infoPlist)) {
  const asarHash = crypto.createHash("sha256").update(fs.readFileSync(asarPath)).digest("hex");
  execFileSync("/usr/libexec/PlistBuddy", [
    "-c",
    `Set :ElectronAsarIntegrity:Resources/app.asar:hash ${asarHash}`,
    infoPlist,
  ]);
}
fs.rmSync(appFolder, { recursive: true, force: true });
fs.writeFileSync(path.join(resourcesDir, "codex-installer-patch.txt"), "Codex API mode fast/plugins/i18n/updater patch applied.", "utf8");
console.log(`  OK: Codex app patch applied and repacked (${patchCount} changes)`);
