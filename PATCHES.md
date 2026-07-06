# Codex Desktop Installer 补丁记录

更新日期: 2026-07-04

本文档记录当前 Codex Desktop Installer 项目中已经实现的补丁、打包改造、运行时改造、验证脚本，以及尚未完成的补丁点。目标是后续换 Codex Desktop 版本、重打包、排查回归时有明确索引。

## 补丁原则

| 原则 | 说明 |
| --- | --- |
| 只使用 Codex 命名 | 禁止旧品牌名出现在用户可见安装入口。 |
| 离线可分发 | 安装包内置 Codex Desktop、启动器、代理、7-Zip、Node、Skills、Plugins。 |
| 不打包真实凭证 | 不复制本机个人 `.codex` 凭证，只生成默认空配置和模型目录。 |
| 安装路径固定 | 默认安装到 `%LOCALAPPDATA%\Programs\Codex`。 |
| 配置路径固定 | 只写 `%USERPROFILE%\.codex` 和 `%APPDATA%\CodexLauncher`。 |
| 不部署 `.agents` | 安装器不把 `.agents` 复制到用户系统。 |
| 免费模式隔离 | 免费模式只显示 4 个 free 模型，只显示 `none` 推理，并通过本地代理转发。 |
| 官方模式保留联网 | OpenAI 官方和自定义 API 不加离线屏蔽参数，不阻断 `chatgpt.com` / `openai.com`。 |

## 状态总览

| 类别 | 状态 | 入口文件 |
| --- | --- | --- |
| Avalonia 安装器 AOT 兼容 | 已完成 | `CodexInstaller.Desktop/CodexInstaller.Desktop.csproj`, `CodexInstaller.Desktop/ViewLocator.cs` |
| 安装部署逻辑 | 已完成 | `CodexInstaller.Core/InstallEngine.cs` |
| 默认 Codex 配置和 MCP | 已完成 | `CodexInstaller.Core/CodexRuntimeConfigurator.cs`, `CodexInstaller.Core/CodexModelCatalog.cs` |
| 启动器 Provider 管理 | 已完成 | `CodexLauncher/ViewModels/MainWindowViewModel.cs` |
| 免费 API 代理 | 已完成 | `CodexApiProxy/Program.cs` |
| Codex app.asar 前端补丁 | 已完成 | `scripts/patch-codex-app.ps1` |
| 离线 Bundle 构建 | 已完成 | `scripts/prepare-bundle.ps1`, `scripts/build-installer.ps1` |
| Sandbox / NetLog / MCP 验证脚本 | 已完成 | `scripts/*.ps1` |
| 中文 UI i18n | 待处理 | `CodexDesktop-Rebuild/src/win/_asar/webview/assets/app-main-BIo-yK5z.js` |

## Avalonia 安装器补丁

| 文件 | 补丁 | 目的 |
| --- | --- | --- |
| `CodexInstaller.Desktop/CodexInstaller.Desktop.csproj` | `TargetFramework=net10.0`, `PublishAot=true`, `ApplicationIcon=Assets\codex.ico` | 生成 .NET 10 Native AOT Windows 安装器并带 Codex 图标。 |
| `CodexInstaller.Desktop/CodexInstaller.Desktop.csproj` | `Compile Remove="Bundle\**"`, `EmbeddedResource Remove="Bundle\**"` | 防止 Bundle 内 `.cs` 文件被安装器项目编译。 |
| `CodexInstaller.Desktop/CodexInstaller.Desktop.csproj` | `Content Include="Bundle\**"` | 发布时携带完整 Bundle。当前文件里有多段重复 Content ItemGroup，功能上可用，但后续可清理。 |
| `CodexInstaller.Desktop/app.manifest` | `requestedExecutionLevel level="requireAdministrator"` | 安装器启动时请求管理员权限。 |
| `CodexInstaller.Desktop/ViewLocator.cs` | 从反射查找 View 改为显式 switch 映射 | 修复 Native AOT 下 `WelcomePage view not found`。 |
| `CodexInstaller.Desktop/Program.cs` | 支持 `--silent-install <dir>` 和 `--log <path>` | 支持 Sandbox / 自动化安装验证。 |
| `CodexInstaller.Desktop/ViewModels/WelcomePageViewModel.cs` | 默认安装目录为 `%LOCALAPPDATA%\Programs\Codex` | 固定安装位置。 |
| `CodexInstaller.Desktop/ViewModels/InstallPageViewModel.cs` | 使用 `InstallEngine` 上报百分比和状态 | 大文件复制/解压时可见进度。 |
| `CodexInstaller.Desktop/ViewModels/CompletePageViewModel.cs` | 完成页启动 `Launcher\Codex 启动.exe` | 用户安装完成后进入启动器，而不是直接打开 Codex。 |

## 安装引擎补丁

| 文件 | 补丁 | 目的 |
| --- | --- | --- |
| `CodexInstaller.Core/InstallEngine.cs` | 优先部署 `Bundle/Archives/CodexDesktop.7z`，否则部署 `Bundle/CodexDesktop` 目录 | 支持压缩包式离线分发，减少安装器体积和复制压力。 |
| `CodexInstaller.Core/InstallEngine.cs` | 使用内置 `Bundle/Tools/7zip/7z.exe` 解压 | 干净机器不依赖系统 7-Zip。 |
| `CodexInstaller.Core/InstallEngine.cs` | 解析 7-Zip 输出百分比并映射到 1-70% | 解压 Codex Desktop 时显示真实进度。 |
| `CodexInstaller.Core/InstallEngine.cs` | 部署 `.codex` 配置到 `%USERPROFILE%\.codex` | 避免把配置写入安装目录。 |
| `CodexInstaller.Core/InstallEngine.cs` | 部署 Skills 到 `%USERPROFILE%\.codex\skills` | 让 Codex 可发现内置 Skills。 |
| `CodexInstaller.Core/InstallEngine.cs` | 部署插件到 `%USERPROFILE%\.codex\plugins\cache` | 让插件走 Codex 插件缓存结构。 |
| `CodexInstaller.Core/InstallEngine.cs` | 从安装目录 `resources/plugins/openai-bundled/plugins` 复制官方内置插件到 cache | 保证 browser / computer-use 等 bundled plugins 可用。 |
| `CodexInstaller.Core/InstallEngine.cs` | 读取 `.codex-plugin/plugin.json` 的 version 后写入 cache 版本目录 | 符合 Codex 插件缓存目录结构。 |
| `CodexInstaller.Core/InstallEngine.cs` | 部署 `Bundle/Tools` 到安装目录并把 `Tools/Node` 加到用户 PATH 和当前进程 PATH | 让插件和 MCP 能找到内置 Node。 |
| `CodexInstaller.Core/InstallEngine.cs` | 规范化 node_modules 中 `%40`, `%2B`, `%24` 编码目录和文件名 | 修复 scoped npm 包路径被编码后无法 import 的问题。 |
| `CodexInstaller.Core/InstallEngine.cs` | 去除 `SKILL.md` UTF-8 BOM | 避免 Skill 解析异常。 |
| `CodexInstaller.Core/InstallEngine.cs` | 原子写入 `config.toml` | 避免写配置中断导致文件损坏。 |
| `CodexInstaller.Core/InstallEngine.cs` | 创建桌面和开始菜单 `Codex 启动.lnk` | 用户入口统一走启动器。 |
| `CodexInstaller.Core/InstallEngine.cs` | 写入 HKCU 卸载项 `CodexDesktopLauncher` | 出现在 Windows 卸载列表。 |

## 默认免费模型配置补丁

| 文件 | 补丁 | 目的 |
| --- | --- | --- |
| `CodexInstaller.Core/InstallEngine.cs` | 默认写 `model_provider = "custom"` | 让免费模式走本地自定义 provider。 |
| `CodexInstaller.Core/InstallEngine.cs` | 默认模型为 `deepseek-v4-flash-free` | 安装后可直接使用免费模型。 |
| `CodexInstaller.Core/InstallEngine.cs` | `available_models` 只包含 4 个免费模型 | 免费模式禁止 GPT / 非 free 模型混入。 |
| `CodexInstaller.Core/InstallEngine.cs` | `model_reasoning_effort = "none"` | 免费模式只使用无推理，避免 UI 和请求进入高级推理路径。 |
| `CodexInstaller.Core/InstallEngine.cs` | `model_catalog_json = "%USERPROFILE%\.codex\codex-launcher-model-catalog.json"` | 让前端模型列表读取本地模型目录。 |
| `CodexInstaller.Core/InstallEngine.cs` | `use_hidden_models = true` | 允许自定义模型目录里的 free 模型出现在选择器。 |
| `CodexInstaller.Core/InstallEngine.cs` | `disable_response_storage = true`, `web_search = "disabled"` | 减少默认外网/存储路径。 |
| `CodexInstaller.Core/InstallEngine.cs` | `[model_providers.custom] base_url = "http://127.0.0.1:17631/v1"` | 免费模式请求统一进本地代理。 |
| `CodexInstaller.Core/InstallEngine.cs` | `requires_openai_auth = false` | 免费模式不需要 OpenAI 登录态。 |
| `CodexInstaller.Core/CodexModelCatalog.cs` | 生成 `codex-launcher-model-catalog.json` | 给 Codex 前端提供本地模型元数据。 |
| `CodexInstaller.Core/CodexModelCatalog.cs` | 每个 free 模型只暴露 `supported_reasoning_levels = none` | UI 不显示推理级别列表。 |

免费模型固定为:

```text
deepseek-v4-flash-free
north-mini-code-free
mimo-v2.5-free
nemotron-3-ultra-free
```

## MCP 和插件配置补丁

| 文件 | 补丁 | 目的 |
| --- | --- | --- |
| `CodexInstaller.Core/CodexRuntimeConfigurator.cs` | 写入 `[mcp_servers.node_repl]` | 默认启用 node_repl MCP。 |
| `CodexInstaller.Core/CodexRuntimeConfigurator.cs` | `command` 指向安装目录的 `resources/cua_node/bin/node_repl.exe` | 不依赖用户全局环境。 |
| `CodexInstaller.Core/CodexRuntimeConfigurator.cs` | 写 `NODE_REPL_NODE_PATH`, `NODE_REPL_NODE_MODULE_DIRS`, `CODEX_HOME` | 固定 Node 和模块解析路径。 |
| `CodexInstaller.Core/CodexRuntimeConfigurator.cs` | 写 `NODE_REPL_TRUSTED_CODE_PATHS` | 信任 `.codex`、安装目录 plugins、安装目录 cua_node。 |
| `CodexInstaller.Core/CodexRuntimeConfigurator.cs` | 计算并写入 `NODE_REPL_TRUSTED_BROWSER_CLIENT_SHA256S` | 允许 browser-client.mjs 被 node_repl 加载。 |
| `CodexInstaller.Core/CodexRuntimeConfigurator.cs` | 写 `BROWSER_USE_AVAILABLE_BACKENDS = "chrome,iab"` | 同时支持 Chrome 和 in-app browser。 |
| `CodexInstaller.Core/CodexRuntimeConfigurator.cs` | 写 `SKY_CUA_NATIVE_PIPE = "1"` | 启用 CUA native pipe。 |
| `CodexInstaller.Core/CodexRuntimeConfigurator.cs` | 写 `js_repl = true`, `tool_search = true` | 默认开启 JS REPL 和工具搜索特性。 |
| `CodexInstaller.Core/CodexRuntimeConfigurator.cs` | 默认启用 `browser@openai-bundled`, `computer-use@openai-bundled`, `hyperframes@openai-curated-remote` | 安装后插件默认可用。 |
| `CodexInstaller.Core/CodexRuntimeConfigurator.cs` | 规范化 `mcp_servers.mcp__node_repl` 为 `mcp_servers.node_repl` | 修复错误 MCP 前缀。 |

## Codex 启动器补丁

| 文件 | 补丁 | 目的 |
| --- | --- | --- |
| `CodexLauncher/ViewModels/MainWindowViewModel.cs` | Provider 分为 `免费模型`, `OpenAI 官方`, `自定义 API` | 用户可明确选择运行模式。 |
| `CodexLauncher/ViewModels/MainWindowViewModel.cs` | 免费模式 Base URL 内部固定为 `https://opencode.ai/zen/v1`，Codex 配置写本地 `http://127.0.0.1:17631/v1` | 启动器管理上游，Codex 只访问本地代理。 |
| `CodexLauncher/ViewModels/MainWindowViewModel.cs` | 免费模式不调用远端 `/models`，直接使用内置 4 模型列表 | 避免无代理/离线时启动卡住。 |
| `CodexLauncher/ViewModels/MainWindowViewModel.cs` | 免费模式只允许 `-free` 模型，按固定优先级排序 | 防止用户选到 GPT 或第三方非 free 模型。 |
| `CodexLauncher/ViewModels/MainWindowViewModel.cs` | 官方模式删除 launcher 写入的 custom provider 和 model catalog | 恢复官方 OpenAI / ChatGPT 行为。 |
| `CodexLauncher/ViewModels/MainWindowViewModel.cs` | 非免费模式不隐藏官方/自定义模型，不启动免费代理 | 保留官方订阅和自定义 API 连接能力。 |
| `CodexLauncher/ViewModels/MainWindowViewModel.cs` | 保存时写 `%USERPROFILE%\.codex\config.toml` 和 `auth.json` | 启动器成为配置入口。 |
| `CodexLauncher/ViewModels/MainWindowViewModel.cs` | 只在免费模式启动 `CodexApiProxy.exe` | 免费模型请求走代理转换层。 |
| `CodexLauncher/ViewModels/MainWindowViewModel.cs` | 免费模式启动 proxy 前清理旧 proxy | 避免端口占用和僵尸代理。 |
| `CodexLauncher/ViewModels/MainWindowViewModel.cs` | 非免费模式清理 proxy | 防止官方/自定义请求误走免费代理。 |
| `CodexLauncher/ViewModels/MainWindowViewModel.cs` | 启动 proxy 时传 `--codex-exe <Codex.exe>` | Codex 退出后 proxy 可自动退出。 |
| `CodexLauncher/ViewModels/MainWindowViewModel.cs` | 启动 Codex 前清理安装目录内 stale `node_repl` | 避免旧 MCP 进程占用/污染。 |
| `CodexLauncher/ViewModels/MainWindowViewModel.cs` | 启动 Codex 时把内置 `Tools/Node` prepend 到 PATH | 让 Codex 子进程优先使用内置 Node。 |
| `CodexLauncher/ViewModels/MainWindowViewModel.cs` | 仅免费模式添加 Chromium 离线安全参数 | 避免免费模式后台外联卡 UI，同时不破坏官方登录。 |
| `CodexLauncher/ViewModels/MainWindowViewModel.cs` | launcher 设置存 `%APPDATA%\CodexLauncher\profiles.ini`，值用 base64 | 保存 provider/profile/API key/model 列表。 |

免费模式 Chromium 参数包含:

```text
--disable-background-networking
--disable-component-update
--disable-domain-reliability
--disable-sync
--disable-client-side-phishing-detection
--disable-features=AutofillServerCommunication,CertificateTransparencyComponentUpdater,OptimizationGuideModelDownloading,OptimizationGuideOnDeviceModel,OptimizationHints,OptimizationHintsFetching,OptimizationTargetPrediction,SegmentationPlatform,MediaRouter
--host-resolver-rules=MAP chat.openai.com 0.0.0.0,MAP chatgpt.com 0.0.0.0,MAP ab.chatgpt.com 0.0.0.0,MAP a.nel.cloudflare.com 0.0.0.0,MAP android.clients.google.com 0.0.0.0,MAP clients2.google.com 0.0.0.0,MAP dl.google.com 0.0.0.0,MAP optimizationguide-pa.googleapis.com 0.0.0.0,MAP redirector.gvt1.com 0.0.0.0,MAP mtalk.google.com 0.0.0.0,EXCLUDE localhost,EXCLUDE 127.0.0.1
```

## CodexApiProxy 补丁

| 文件 | 补丁 | 目的 |
| --- | --- | --- |
| `CodexApiProxy/Program.cs` | 支持 `--port`, `--upstream`, `--api-key`, `--codex-pid`, `--parent-pid`, `--codex-exe` | 允许启动器控制监听端口、上游和生命周期。 |
| `CodexApiProxy/Program.cs` | `Local\CodexApiProxy_{port}` mutex | 同端口只允许一个代理实例。 |
| `CodexApiProxy/Program.cs` | 监控 Codex PID 或 Codex.exe 进程 | Codex 退出后代理自动退出。 |
| `CodexApiProxy/Program.cs` | `/health` 和 `/v1/health` | 启动器可检查代理健康。 |
| `CodexApiProxy/Program.cs` | 免费上游且无 API key 时，本地返回 `/models` | `/v1/models` 不访问 `opencode.ai`，启动更快且离线可用。 |
| `CodexApiProxy/Program.cs` | 免费请求不继承用户 OpenAI/Codex Authorization header | 避免把官方登录凭证带到免费上游。 |
| `CodexApiProxy/Program.cs` | OpenAI Responses API 转 Chat Completions API | 适配 free 上游的 `/chat/completions`。 |
| `CodexApiProxy/Program.cs` | 支持 `input`, `messages`, `instructions` 转换为 chat `messages` | 兼容 Codex 发出的不同请求结构。 |
| `CodexApiProxy/Program.cs` | 支持 `function_call` 和 `function_call_output` 往返转换 | 让 MCP 工具调用链路可用。 |
| `CodexApiProxy/Program.cs` | 支持 `tool_search` 特殊工具 | 让动态工具发现能通过 free 上游。 |
| `CodexApiProxy/Program.cs` | 支持 namespace dynamic tools 回填 | 把上游 tool call 解析回 Codex 需要的 namespace/name。 |
| `CodexApiProxy/Program.cs` | Chat Completions 响应转 Responses JSON | Codex 前端继续接收 Responses 结构。 |
| `CodexApiProxy/Program.cs` | Streaming 请求返回 Responses SSE 事件序列 | 兼容 Codex 的 stream 消费路径。 |
| `CodexApiProxy/Program.cs` | 上游错误写 `%APPDATA%\CodexLauncher\proxy-errors` | 排查上游错误时保留原始响应。 |
| `CodexApiProxy/Program.cs` | debug log 写 `%APPDATA%\CodexLauncher\proxy-debug.log` | 排查工具/模型请求转换。 |
| `CodexApiProxy/Program.cs` | `SocketsHttpHandler.ConnectTimeout = 15s` | 避免无网络时连接长期挂住。 |
| `CodexApiProxy/Program.cs` | `--self-test` | 验证 function_call/tool_result 转换逻辑。 |

## Codex app.asar 前端补丁

补丁入口: `scripts/patch-codex-app.ps1`

补丁方式: 解包 `resources/app.asar` 到 `resources/app`，修改 `webview/assets/*.js` 和 bundled plugin `SKILL.md`，再重新 pack 回 `resources/app.asar`。

| 目标文件模式 | 补丁标签 | 行为 |
| --- | --- | --- |
| `read-service-tier-for-request-*.js` | `fast mode request gate` | 移除 fast mode 只允许 `chatgpt` 的限制。 |
| `use-service-tier-settings-*.js` | `fast mode settings gate` | 将 fast mode settings gate 强制设为 true。 |
| `use-is-plugins-enabled-*.js` | `plugins availability gate` | 插件启用时直接返回 available，绕过 platform/statsig/feature gate。 |
| `use-plugin-install-flow-*.js` | `connector availability item gate` | 禁用 connector unavailable item gate。 |
| `use-plugin-install-flow-*.js` | `connector availability plugin gate` | 只保留 admin 禁用逻辑，不因 connector unavailable 禁用插件。 |
| `*.js` | `apikey plugin gate` | 修改 apikey 模式下的插件 gate，避免 API key profile 错误进入官方插件限制路径。 |
| `model-list-filter-*.js` | `free model mode gate` | 根据当前模型是否 `-free` 分离免费/非免费模型列表。 |
| `model-list-filter-*.js` | `free model fixed catalog` | 免费模式强制注入 4 个 free 模型。 |
| `model-list-filter-*.js` | `free model fixed catalog` | 免费模式 `hasModelSupportingMaxReasoningEffort=false`, `hasModelSupportingUltraReasoningEffort=false`。 |
| `model-and-reasoning-dropdown-*.js` | `free model dropdown` | 免费模式 dropdown 只显示 4 个 free 模型，推理固定为 `none`。 |
| `composer-*.js` | `free model composer label` | 输入框模型选择器闭合态显示免费模型名称和 `none` 推理。 |
| `resources/plugins/openai-bundled/plugins/**/SKILL.md` | `browser client import guidance` | 补充 Windows 下动态 import `browser-client.mjs` 需使用 `file:///C:/...` 或正斜杠路径。 |
| `resources/app/codex-installer-patch.txt` | marker | 写入补丁标记。 |
| `resources/codex-installer-patch.txt` | marker | 写入补丁标记。 |

当前已验证过的前端补丁文件包括:

```text
CodexDesktop-Rebuild/src/win/_asar/webview/assets/model-list-filter-BHZwEh8K.js
CodexDesktop-Rebuild/src/win/_asar/webview/assets/model-and-reasoning-dropdown-CEtoHn2j.js
CodexDesktop-Rebuild/src/win/_asar/webview/assets/composer-Db3QCHFq.js
```

关联但不是直接字符串替换的文件:

```text
CodexDesktop-Rebuild/src/win/_asar/webview/assets/model-queries-CjDMp6EZ.js
```

`model-queries-CjDMp6EZ.js` 会 import `model-list-filter-BHZwEh8K.js`，因此 free model 过滤结果会进入模型查询链路；当前没有在该文件内直接打硬编码 free model 补丁。

`patch-codex-app.ps1` 还有 idempotent 检查: 如果 `model-list-filter-*.js` 没有成功应用 free model patch，会抛出错误，避免继续打出未过滤模型的包。

## Bundle 和构建脚本补丁

| 文件 | 补丁 | 目的 |
| --- | --- | --- |
| `scripts/build-installer.ps1` | `-SkipRebuild` | 允许跳过 Codex Desktop 重建，仅重打 Bundle / 安装器。 |
| `scripts/build-installer.ps1` | `Invoke-NativeChecked` | 捕获 native 命令 exit code，避免 PowerShell 假成功。 |
| `scripts/build-installer.ps1` | `Add-SevenZipShimToPath` | 给 CodexDesktop-Rebuild 构建提供 `7zz.cmd` shim，修复 `7zz is not recognized`。 |
| `scripts/build-installer.ps1` | 自动 clone / npm install / `npm run build:win-x64` | 可从源码重建 Codex Desktop。 |
| `scripts/build-installer.ps1` | 同步 `Bundle` 到 `CodexInstaller.Desktop/Bundle` | 发布安装器时携带 Bundle。 |
| `scripts/build-installer.ps1` | AOT publish 安装器并重命名为 `Codex 安装.exe` | 用户可见安装程序命名统一。 |
| `scripts/prepare-bundle.ps1` | 检查 `$RebuildOutputDir/Codex.exe` 必须存在 | 防止把不完整 Codex Desktop 输出打进包。 |
| `scripts/prepare-bundle.ps1` | 复制 Codex Desktop 后立即运行 `patch-codex-app.ps1` | 保证 asar 前端补丁进入 Bundle。 |
| `scripts/prepare-bundle.ps1` | 内置 7-Zip | 安装和 Sandbox 解压不依赖系统。 |
| `scripts/prepare-bundle.ps1` | 内置 Node.js runtime | 插件和工具不依赖用户环境。 |
| `scripts/prepare-bundle.ps1` | 内置 VC++ runtime DLL 到 CUA Node 相关目录 | 干净 Windows 上降低 native 模块缺运行库风险。 |
| `scripts/prepare-bundle.ps1` | 规范化 scoped node_modules 编码名称 | 修复离线复制后的 npm 包路径。 |
| `scripts/prepare-bundle.ps1` | 用 7-Zip LZMA2 压缩 `CodexDesktop.7z`，压缩后清空 `Bundle/CodexDesktop` | 降低安装器体积。 |
| `scripts/prepare-bundle.ps1` | Native AOT 发布 `CodexLauncher` 和 `CodexApiProxy` | 运行时不依赖 .NET runtime。 |
| `scripts/prepare-bundle.ps1` | `CodexLauncher.exe` 重命名为 `Codex 启动.exe` | 用户入口命名统一。 |
| `scripts/prepare-bundle.ps1` | 不复制本机个人 `.codex` | 避免把凭证/私有配置打进包。 |
| `scripts/prepare-bundle.ps1` | 复制 canghe Skills，移除 `.git` | 内置 Skills 且不带 git 元数据。 |
| `scripts/prepare-bundle.ps1` | 复制 HyperFrames by HeyGen 插件，移除 `.git` | 内置常用插件。 |

## 验证和排障脚本

| 文件 | 用途 | 覆盖点 |
| --- | --- | --- |
| `scripts/run-sandbox-smoke-test.ps1` | Windows Sandbox 安装冒烟测试 | silent install、Codex.exe、launcher、proxy、Node、node_repl、MCP handshake、browser-client load、proxy self-test、默认 free config、插件 enabled、Node PATH。 |
| `scripts/test-browser-use-host.ps1` | 本机 MCP / browser-use host 验证 | node_repl 初始化、JS tool exposed、JS 执行、browser-client 动态加载、可选 in-app browser 选择。 |
| `scripts/start-codex-netlog.ps1` | 本机启动 Codex 并采集 Chromium NetLog | 加 `--log-net-log`, `--net-log-capture-mode=Everything`, `--remote-debugging-port=9227`。 |
| `scripts/analyze-codex-netlog.ps1` | 分析 NetLog | 列出 failed requests、hosts、net_error；JSON 截断时有文本 fallback。 |
| `scripts/run-windows-sandbox-netlog.ps1` | Sandbox 中解包安装包并抓 NetLog | 主机先解压 zip，Sandbox 内直接解 `CodexDesktop.7z`，写最小 free config，启动 proxy 和 Codex。 |
| `scripts/quick-debug.ps1` | Debug 快捷构建/运行 | 同步 Bundle、Debug build、可启动安装器或启动器。 |

已验证过的关键结果:

```text
CodexApiProxy --self-test passed
node_repl mcp handshake PASS
node_repl js tool exposed PASS
node_repl js executes PASS
browser client loads PASS
/v1/models local free model count = 4
free model selector only shows 4 free models
free mode reasoning only shows none / 无
```

## 已知待处理补丁

| 项 | 状态 | 说明 |
| --- | --- | --- |
| 中文 UI i18n | 待处理 | Codex 已识别 `zh-CN`，设置页也显示中文语言，但界面仍显示英文。 |
| i18n 根因 | 已定位方向 | `app-main-BIo-yK5z.js` 顶层 provider 计算出 locale 后，只有隐藏开关 `enable_i18n` 为真时才加载 locale messages。 |
| 资源状态 | 已确认 | `webview/assets/zh-CN-IPfEBMJT.js` 和 `native-menu-locales/zh-CN.json` 存在。 |
| 推荐补丁 | 未应用 | 在非默认 locale 或 `zh-CN` 时强制加载 `Lg(b)` messages，不再依赖隐藏 `enable_i18n`。 |
| 验证方式 | 未完成 | 用 CDP 检查 loaded chunks、IntlProvider messages、界面文本是否从英文 defaultMessage 切换到中文。 |

相关文件:

```text
CodexDesktop-Rebuild/src/win/_asar/webview/assets/app-main-BIo-yK5z.js
CodexDesktop-Rebuild/src/win/_asar/webview/assets/locale-resolver-Nf5W7uMs.js
CodexDesktop-Rebuild/src/win/_asar/webview/assets/general-settings-BYSDERl_.js
CodexDesktop-Rebuild/src/win/_asar/webview/assets/settings-page-cYvzgR-H.js
CodexDesktop-Rebuild/src/win/_asar/webview/assets/zh-CN-IPfEBMJT.js
CodexDesktop-Rebuild/src/win/_asar/native-menu-locales/zh-CN.json
```

## 重新打包顺序

推荐顺序:

```powershell
pwsh scripts/build-installer.ps1 -SkipRebuild
```

如果完整脚本因大量复制输出中断，可手动执行等价步骤:

```text
1. 确认 Codex Desktop 完整安装目录里存在 Codex.exe。
2. 运行或手动应用 scripts/patch-codex-app.ps1。
3. 用 asar 重新 pack resources/app.asar。
4. 重打 Bundle/Archives/CodexDesktop.7z。
5. 同步 CodexDesktop.7z 到 CodexInstaller.Desktop/Bundle/Archives。
6. Native AOT 发布 CodexLauncher 和 CodexApiProxy 到 Bundle/Launcher。
7. Native AOT 发布 CodexInstaller.Desktop。
8. 重打 dist/CodexInstaller.zip。
9. 运行 Sandbox smoke test 和 NetLog 验证。
```

## 回归检查清单

| 检查项 | 期望 |
| --- | --- |
| 安装目录 | `%LOCALAPPDATA%\Programs\Codex\Codex.exe` 存在。 |
| 启动器 | `%LOCALAPPDATA%\Programs\Codex\Launcher\Codex 启动.exe` 存在。 |
| 代理 | `%LOCALAPPDATA%\Programs\Codex\Launcher\CodexApiProxy.exe` 存在。 |
| 免费 config | `.codex/config.toml` 默认模型为 `deepseek-v4-flash-free`。 |
| 免费模型列表 | 只显示 4 个 `-free` 模型。 |
| 免费推理 | 只显示 `none` / `无`。 |
| 免费 `/v1/models` | 本地返回 4 个模型，不访问上游。 |
| 官方模式 | 不加离线 host-resolver 屏蔽，OpenAI/ChatGPT 连接能力保留。 |
| MCP | `node_repl` handshake、JS tool、browser-client load 通过。 |
| Proxy 生命周期 | 同端口单实例，Codex 退出后 proxy 退出。 |
| app.asar | `resources/codex-installer-patch.txt` 存在或前端行为验证通过。 |
