using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Net;
using System.Text;
using System.Security.Cryptography;

class OpenCodeTelegramLauncher
{
    static Process bot;
    static Process server;
    static bool stopping;
    static Mutex singleInstanceMutex;
    static bool ownsMutex;
    static bool botRecoveryInProgress;
    static bool eot10Enabled;
    static string eot10Password = "";
    static bool eot10MigratedLegacyPassword;
    static DateTime lastBotStartAttemptUtc = DateTime.MinValue;
    static readonly TimeSpan BotStartCooldown = TimeSpan.FromSeconds(90);

    static string RunBotCli(string botCmd, string arguments, int timeoutMs)
    {
        try
        {
            using (var p = Process.Start(new ProcessStartInfo {
                FileName = "cmd.exe",
                Arguments = "/d /c \"\"" + botCmd + "\" " + arguments + "\"",
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }))
            {
                StringBuilder output = new StringBuilder();
                StringBuilder error = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (error) error.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { Process.Start(new ProcessStartInfo { FileName = "taskkill.exe", Arguments = "/PID " + p.Id + " /T /F", UseShellExecute = false, CreateNoWindow = true }).WaitForExit(5000); } catch { }
                    return "";
                }
                p.WaitForExit(); // Flush async output events after process exit.
                lock (output)
                lock (error)
                    return output.ToString() + Environment.NewLine + error.ToString();
            }
        }
        catch { return ""; }
    }

    static bool IsBotDaemonRunning(string botCmd)
    {
        string status = RunBotCli(botCmd, "status", 10000);
        return status.IndexOf("Service status: running", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static void RestartBotDaemon(string botCmd, string reason)
    {
        if (stopping) return;
        if (botRecoveryInProgress) return;
        if (DateTime.UtcNow - lastBotStartAttemptUtc < BotStartCooldown)
        {
            Console.WriteLine("[监测] Bot 正在启动或等待连接，暂不重复创建实例。");
            return;
        }
        botRecoveryInProgress = true;
        lastBotStartAttemptUtc = DateTime.UtcNow;
        try
        {
        Console.WriteLine("[恢复] " + reason + "，正在重新连接 Telegram Bot...");
        RunBotCli(botCmd, "stop", 20000);
        if (stopping) return;
        string started = RunBotCli(botCmd, "start --daemon --mode installed", 20000);
        if (started.IndexOf("started", StringComparison.OrdinalIgnoreCase) >= 0 || IsBotDaemonRunning(botCmd))
            Console.WriteLine("[恢复] Telegram Bot 已重新启动。");
        else
            Console.WriteLine("[恢复] Bot 未确认启动成功，将继续监测并重试。");
        }
        finally { botRecoveryInProgress = false; }
    }

    static bool IsOpenCodeServerHealthy()
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:4096/global/health");
            request.Method = "GET";
            request.Timeout = 2000;
            request.ReadWriteTimeout = 2000;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                string body = reader.ReadToEnd();
                return response.StatusCode == HttpStatusCode.OK && body.IndexOf("healthy", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch { return false; }
    }

    static bool PatchInstalledFile(string path, string marker, Regex pattern, string replacement)
    {
        if (!File.Exists(path)) return false;
        string content = File.ReadAllText(path, Encoding.UTF8);
        if (content.IndexOf(marker, StringComparison.Ordinal) >= 0) return true;
        Match match = pattern.Match(content);
        if (!match.Success) return false;
        content = pattern.Replace(content, replacement, 1);
        string backupPath = path + ".codex-original";
        if (!File.Exists(backupPath)) File.Copy(path, backupPath, false);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return true;
    }

    static string ReadEot10CryptoSource()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.EndsWith("EOT10Crypto.mjs", StringComparison.OrdinalIgnoreCase)) continue;
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) continue;
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }
        return null;
    }

    static string InstalledBotEntryPath(string appData)
    {
        return Path.Combine(appData, "npm", "node_modules", "@grinev", "opencode-telegram-bot", "dist", "bot", "index.js");
    }

    static bool EnsureInstalledBotEntryAvailable(string appData)
    {
        string botIndex = InstalledBotEntryPath(appData);
        if (File.Exists(botIndex)) return true;

        Console.WriteLine("[修复] npm 更新后没有找到 Bot 入口文件，尝试重新安装当前 latest 版本...");
        int exitCode;
        string error;
        RunNpm("install --loglevel=error --global @grinev/opencode-telegram-bot@latest", 300000, out exitCode, out error);
        if (File.Exists(botIndex))
        {
            Console.WriteLine("[修复] Bot 入口已恢复。");
            return true;
        }
        Console.WriteLine("[修复] Bot 入口仍不存在：" + botIndex);
        if (!String.IsNullOrWhiteSpace(error)) Console.WriteLine("npm 错误：" + error.Trim());
        return false;
    }

    static bool EnsureGenericDocumentForwarding(string appData)
    {
        try
        {
            string documentHandlerPath = Path.Combine(appData, "npm", "node_modules", "@grinev", "opencode-telegram-bot", "dist", "bot", "handlers", "document-handler.js");
            if (!File.Exists(documentHandlerPath))
            {
                Console.WriteLine("[文件] 找不到 Bot 文档处理器，无法启用通用文件转交。");
                return false;
            }
            string content = File.ReadAllText(documentHandlerPath, Encoding.UTF8);
            const string previousMimeMarker = "OpenCode direct attachment forwarding with MIME inference";
            const string previousRawMarker = "OpenCode raw workspace attachment forwarding";
            const string previousLocalMarker = "OpenCode workspace-local raw attachment access";
            const string previousLegacyMarker = "OpenCode generic all-extension file forwarding";
            const string previousHybridMarker = "OpenCode hybrid direct-and-local attachment handling";
            if (content.IndexOf(previousMimeMarker, StringComparison.Ordinal) >= 0 ||
                content.IndexOf(previousRawMarker, StringComparison.Ordinal) >= 0 ||
                content.IndexOf(previousLocalMarker, StringComparison.Ordinal) >= 0 ||
                content.IndexOf(previousLegacyMarker, StringComparison.Ordinal) >= 0 ||
                content.IndexOf(previousHybridMarker, StringComparison.Ordinal) >= 0)
            {
                string legacyBackup = documentHandlerPath + ".opencode-all-files-original";
                if (!File.Exists(legacyBackup))
                {
                    Console.WriteLine("[文件] 检测到旧版附件补丁但缺少原文件备份，无法安全切换到本地读取模式。");
                    return false;
                }
                File.Copy(legacyBackup, documentHandlerPath, true);
                content = File.ReadAllText(documentHandlerPath, Encoding.UTF8);
            }
            const string marker = "OpenCode hybrid attachment handling with UTF-8 detection";
            if (content.IndexOf(marker, StringComparison.Ordinal) >= 0) return true;

            string newline = content.Contains("\r\n") ? "\r\n" : "\n";
            string anchor = "    try {" + newline + "        if (isTextMimeType(mimeType, filename)) {";
            string replacement = "    try {" + newline +
                "        // " + marker + ": use OpenCode file parts for recognized types; save only unrecognized binary formats for local tool access." + newline +
                "        await ctx.reply(t(\"bot.file_downloading\"));" + newline +
                "        if (await rejectQueuedMediaBeforePreparation(ctx, doc.file_size)) return;" + newline +
                "        const genericAttachmentFile = await downloadFile(ctx.api, doc.file_id);" + newline +
                "        const extensionMime = ({ pdf: \"application/pdf\", png: \"image/png\", jpg: \"image/jpeg\", jpeg: \"image/jpeg\", gif: \"image/gif\", webp: \"image/webp\", bmp: \"image/bmp\", tif: \"image/tiff\", tiff: \"image/tiff\", mp3: \"audio/mpeg\", wav: \"audio/wav\", m4a: \"audio/mp4\", ogg: \"audio/ogg\", mp4: \"video/mp4\", mov: \"video/quicktime\", webm: \"video/webm\", txt: \"text/plain\", md: \"text/markdown\", csv: \"text/csv\", json: \"application/json\", xml: \"application/xml\", html: \"text/html\", htm: \"text/html\", js: \"text/javascript\", ts: \"text/typescript\", py: \"text/x-python\", c: \"text/x-c\", h: \"text/x-c\", cpp: \"text/x-c++\", java: \"text/x-java\", log: \"text/plain\", doc: \"application/msword\", docx: \"application/vnd.openxmlformats-officedocument.wordprocessingml.document\", rtf: \"application/rtf\", odt: \"application/vnd.oasis.opendocument.text\", ppt: \"application/vnd.ms-powerpoint\", pptx: \"application/vnd.openxmlformats-officedocument.presentationml.presentation\", xls: \"application/vnd.ms-excel\", xlsx: \"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet\", zip: \"application/zip\", gz: \"application/gzip\", tar: \"application/x-tar\", rar: \"application/vnd.rar\", sevenz: \"application/x-7z-compressed\", \"7z\": \"application/x-7z-compressed\" })[path.extname(filename).slice(1).toLowerCase()];" + newline +
                "        const attachmentMime = !mimeType || mimeType === \"application/octet-stream\" ? (extensionMime || \"application/octet-stream\") : mimeType;" + newline +
                "        let detectedText = null;" + newline +
                "        if (attachmentMime === \"application/octet-stream\" && genericAttachmentFile.buffer.length > 0) {" + newline +
                "            try {" + newline +
                "                const decoded = new TextDecoder(\"utf-8\", { fatal: true }).decode(genericAttachmentFile.buffer);" + newline +
                "                if (!/[\\x00-\\x08\\x0b\\x0c\\x0e-\\x1f]/.test(decoded)) detectedText = decoded;" + newline +
                "            } catch { /* Invalid UTF-8 remains a binary attachment. */ }" + newline +
                "        }" + newline +
                "        if (detectedText !== null || isTextMimeType(attachmentMime, filename)) {" + newline +
                "            const textContent = detectedText !== null ? detectedText : genericAttachmentFile.buffer.toString(\"utf-8\");" + newline +
                "            await submitPrompt(`--- Content of ${filename} ---\\n${textContent}\\n--- End of file ---\\n\\n${caption}`, [], doc.file_size || genericAttachmentFile.buffer.length);" + newline +
                "            return;" + newline +
                "        }" + newline +
                "        const directMediaType = attachmentMime !== \"application/octet-stream\";" + newline +
                "        if (directMediaType) {" + newline +
                "            const attachmentDataUri = toDataUri(genericAttachmentFile.buffer, attachmentMime);" + newline +
                "            await submitPrompt(caption, [{ type: \"file\", mime: attachmentMime, filename, url: attachmentDataUri }], doc.file_size || genericAttachmentFile.buffer.length);" + newline +
                "            return;" + newline +
                "        }" + newline +
                "        const activeProject = getCurrentProject();" + newline +
                "        if (!activeProject?.worktree) { await ctx.reply(t(\"bot.project_not_selected\")); return; }" + newline +
                "        const attachmentDirectory = path.join(activeProject.worktree, \".opencode-telegram-attachments\");" + newline +
                "        await mkdir(attachmentDirectory, { recursive: true });" + newline +
                "        const safeFilename = path.basename(filename).replace(/[<>:\\\"\\\\|?*\\x00-\\x1f]/g, \"_\") || \"document\";" + newline +
                "        const storedFilename = `${Date.now()}-${Math.random().toString(36).slice(2, 8)}-${safeFilename}`;" + newline +
                "        const storedPath = path.join(attachmentDirectory, storedFilename);" + newline +
                "        await writeFile(storedPath, genericAttachmentFile.buffer);" + newline +
                "        const relativePath = path.relative(activeProject.worktree, storedPath).split(path.sep).join(\"/\");" + newline +
                "        const attachmentPrompt = `${caption.trim()}\\n\\nTelegram 附件已原样保存到当前项目文件：${relativePath}。该格式没有通过 OpenCode 的直接附件类型识别，请用可用的本地文件/命令工具读取并分析；若格式需要解析器，请尝试本机已有工具。`;" + newline +
                "        await submitPrompt(attachmentPrompt.trim(), [], doc.file_size || genericAttachmentFile.buffer.length);" + newline +
                "        return;" + newline +
                "        if (isTextMimeType(mimeType, filename)) {";
            string importsAnchor = "import { config } from \"../../config.js\";";
            string importsReplacement = importsAnchor + newline +
                "import { mkdir, writeFile } from \"node:fs/promises\";" + newline +
                "import path from \"node:path\";" + newline +
                "import { getCurrentProject } from \"../../app/stores/settings-store.js\";";
            if (content.IndexOf(importsAnchor, StringComparison.Ordinal) < 0)
            {
                Console.WriteLine("[文件] 当前 Bot 文档处理器缺少预期导入位置，无法启用项目内附件读取。");
                return false;
            }
            if (content.IndexOf(anchor, StringComparison.Ordinal) < 0)
            {
                Console.WriteLine("[文件] 当前 Bot 文档处理器结构已变化，无法安全接入通用文件转交。");
                return false;
            }
            content = content.Replace(importsAnchor, importsReplacement);
            string backupPath = documentHandlerPath + ".opencode-all-files-original";
            if (!File.Exists(backupPath)) File.Copy(documentHandlerPath, backupPath, false);
            File.WriteAllText(documentHandlerPath, content.Replace(anchor, replacement), new UTF8Encoding(false));
            Console.WriteLine("[文件] 已启用已知类型直接发送、未知文本检测及未知二进制文件本地读取。");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[文件] 无法启用通用文件转交：" + ex.Message);
            return false;
        }
    }

    static bool EnsureEot10Encryption(string appData)
    {
        try
        {
            if (String.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENCODE_TELEGRAM_EOT10_PASSWORD")))
            {
                Console.WriteLine("[加密] 尚未提供本次运行的 EOT10 密码。");
                return false;
            }
            string dist = Path.Combine(appData, "npm", "node_modules", "@grinev", "opencode-telegram-bot", "dist", "bot");
            string botIndex = InstalledBotEntryPath(appData);
            string cryptoText = ReadEot10CryptoSource();
            string targetCrypto = Path.Combine(dist, "eot10-crypto.mjs");
            if (String.IsNullOrEmpty(cryptoText))
            {
                Console.WriteLine("[加密] 找不到 EOT10Crypto.mjs：既未随启动器提供，也未内嵌于 EXE。");
                return false;
            }
            if (!File.Exists(botIndex))
            {
                Console.WriteLine("[加密] 找不到 Bot 入口文件：" + botIndex);
                return false;
            }
            if (!File.Exists(targetCrypto) || File.ReadAllText(targetCrypto, Encoding.UTF8) != cryptoText)
                File.WriteAllText(targetCrypto, cryptoText, new UTF8Encoding(false));

            string fileDownloadPath = Path.GetFullPath(Path.Combine(dist, "..", "app", "services", "file-download-service.js"));
            if (!File.Exists(fileDownloadPath))
            {
                Console.WriteLine("[加密] 找不到 Bot 文件下载服务，无法安全接入加密媒体。");
                return false;
            }
            string fileDownloadContent = File.ReadAllText(fileDownloadPath, Encoding.UTF8);
            if (fileDownloadContent.IndexOf("EOT10 decrypted media cache", StringComparison.Ordinal) < 0)
            {
                string fileImportAnchor = "import nodeFetch from \"node-fetch\";";
                string fileNewline = fileDownloadContent.Contains("\r\n") ? "\r\n" : "\n";
                string fileImportReplacement = fileImportAnchor + fileNewline + "import { takeDecryptedEot10Media } from \"../../bot/eot10-crypto.mjs\";";
                string fileFunctionAnchor = "export async function downloadTelegramFile(api, fileId) {";
                string fileFunctionReplacement = fileFunctionAnchor + fileNewline +
                    "    // EOT10 decrypted media cache: consume the authenticated local plaintext prepared by the early middleware." + fileNewline +
                    "    const eot10Media = takeDecryptedEot10Media(fileId);" + fileNewline +
                    "    if (eot10Media) return eot10Media;";
                if (fileDownloadContent.IndexOf(fileImportAnchor, StringComparison.Ordinal) < 0 || fileDownloadContent.IndexOf(fileFunctionAnchor, StringComparison.Ordinal) < 0)
                {
                    Console.WriteLine("[加密] 当前 Bot 文件下载服务结构已变化，无法安全接入加密媒体。");
                    return false;
                }
                fileDownloadContent = fileDownloadContent.Replace(fileImportAnchor, fileImportReplacement)
                    .Replace(fileFunctionAnchor, fileFunctionReplacement);
                string fileDownloadBackup = fileDownloadPath + ".eot10-original";
                if (!File.Exists(fileDownloadBackup)) File.Copy(fileDownloadPath, fileDownloadBackup, false);
                File.WriteAllText(fileDownloadPath, fileDownloadContent, new UTF8Encoding(false));
            }

            string content = File.ReadAllText(botIndex, Encoding.UTF8);
            string newline = content.Contains("\r\n") ? "\r\n" : "\n";
            if (content.IndexOf("EOT10 desktop transport", StringComparison.Ordinal) >= 0 &&
                content.IndexOf("OPENCODE_TELEGRAM_EOT10_ENABLED", StringComparison.Ordinal) < 0)
            {
                string oldBackup = botIndex + ".eot10-original";
                if (!File.Exists(oldBackup))
                {
                    Console.WriteLine("[加密] 发现旧版 EOT10 补丁但找不到原始备份，无法安全升级补丁。");
                    return false;
                }
                File.Copy(oldBackup, botIndex, true);
                content = File.ReadAllText(botIndex, Encoding.UTF8);
                newline = content.Contains("\r\n") ? "\r\n" : "\n";
            }
            if (content.IndexOf("EOT10 desktop transport", StringComparison.Ordinal) >= 0 &&
                 (content.IndexOf("EOT10 runtime environment status", StringComparison.Ordinal) < 0 ||
                  content.IndexOf("EOT10 command entity compatibility", StringComparison.Ordinal) < 0 ||
                  content.IndexOf("EOT10 disabled-message guard", StringComparison.Ordinal) < 0 ||
                  content.IndexOf("EOT10 rich block encryption", StringComparison.Ordinal) < 0 ||
                  content.IndexOf("EOT10 media upload encryption", StringComparison.Ordinal) < 0 ||
                  content.IndexOf("EOT10 media groups encrypted", StringComparison.Ordinal) < 0))
            {
                string previousBackup = botIndex + ".eot10-original";
                if (!File.Exists(previousBackup))
                {
                    Console.WriteLine("[加密] 当前 EOT10 补丁缺少原始备份，无法安全刷新运行状态诊断。");
                    return false;
                }
                File.Copy(previousBackup, botIndex, true);
                content = File.ReadAllText(botIndex, Encoding.UTF8);
                newline = content.Contains("\r\n") ? "\r\n" : "\n";
            }
            if (content.IndexOf("EOT10 desktop transport", StringComparison.Ordinal) < 0)
            {
                string importPattern = "import { Bot } from \"grammy\";";
                if (content.IndexOf(importPattern, StringComparison.Ordinal) < 0)
                {
                    Console.WriteLine("[加密] 当前 Bot 版本缺少预期的 grammy 导入位置，无法安装 EOT10 适配。");
                    return false;
                }
                content = content.Replace(importPattern, importPattern + newline +
                    "import { decryptEot10, encryptEot10, encryptEot10RichMessage, isEot10Message, decryptEot10MediaBuffer, isEot10MediaDocument, cacheDecryptedEot10Media, encryptEot10MediaInput } from \"./eot10-crypto.mjs\";" + newline +
                    "import { downloadTelegramFile as downloadEot10TelegramFile } from \"../app/services/file-download-service.js\";" + newline +
                    "import { InputFile } from \"grammy\";");

                string apiAnchor = "            const runCall = async () => {\n                const response = await prev(method, payload, signal);";
                 string apiReplacement = "            // EOT10 desktop transport: enable only when explicitly configured at launcher startup.\n            const eot10Enabled = process.env.OPENCODE_TELEGRAM_EOT10_ENABLED === \"1\" && Boolean(process.env.OPENCODE_TELEGRAM_EOT10_PASSWORD);\n            const targetBotChat = eot10Enabled && String(payload.chat_id ?? \"\") === String(config.telegram.allowedUserId);\n            const securedPayload = targetBotChat ? { ...payload } : payload;\n            let securedMethod = method;\n            const encryptMarkupText = value => Array.isArray(value) ? value.map(encryptMarkupText) : value && typeof value === \"object\" ? Object.fromEntries(Object.entries(value).map(([key, item]) => [key, key === \"text\" && typeof item === \"string\" ? encryptEot10(item) : encryptMarkupText(item)])) : value;\n            if (targetBotChat && typeof securedPayload.text === \"string\") securedPayload.text = encryptEot10(securedPayload.text);\n            else if (targetBotChat && securedPayload.text && typeof securedPayload.text === \"object\") securedPayload.text = encryptEot10(JSON.stringify(securedPayload.text));\n            if (targetBotChat && typeof securedPayload.caption === \"string\") securedPayload.caption = encryptEot10(securedPayload.caption);\n            if (targetBotChat && securedPayload.reply_markup) securedPayload.reply_markup = encryptMarkupText(securedPayload.reply_markup);\n            if (targetBotChat && securedPayload.rich_message) securedPayload.rich_message = encryptEot10RichMessage(securedPayload.rich_message);\n            const runCall = async () => {\n                if (targetBotChat && method === \"sendMediaGroup\") throw new Error(\"EOT10 media groups must be sent as individually encrypted documents\");\n                const mediaField = ({ sendPhoto: \"photo\", sendDocument: \"document\", sendAudio: \"audio\", sendVoice: \"voice\", sendVideo: \"video\", sendAnimation: \"animation\", sendVideoNote: \"video_note\" })[method];\n                if (targetBotChat && mediaField && securedPayload[mediaField] != null) {\n                    const encryptedMedia = await encryptEot10MediaInput(securedPayload[mediaField]);\n                    securedPayload.document = new InputFile(encryptedMedia.buffer, encryptedMedia.filename);\n                    delete securedPayload[mediaField];\n                    securedMethod = \"sendDocument\";\n                }\n                const response = await prev(securedMethod, securedPayload, signal);";
                apiReplacement = Regex.Replace(apiReplacement, @"            const encryptMarkupText = .*?\n", "");
                apiReplacement = apiReplacement.Replace("            if (targetBotChat && securedPayload.reply_markup) securedPayload.reply_markup = encryptMarkupText(securedPayload.reply_markup);\n", "");
                apiReplacement = apiReplacement.Replace("// EOT10 desktop transport: enable only when explicitly configured at launcher startup.", "// EOT10 desktop transport: enable only when explicitly configured at launcher startup.\n            // EOT10 rich block encryption: preserve layout structure and leave button labels readable.\n            // EOT10 media upload encryption: convert outgoing file bytes to opaque EOT10M documents.");
                apiReplacement = apiReplacement.Replace("// EOT10 media upload encryption: convert outgoing file bytes to opaque EOT10M documents.", "// EOT10 media upload encryption: convert outgoing file bytes to opaque EOT10M documents.\n            // EOT10 media groups encrypted: wrap every item and discard plaintext thumbnails.");
                string oldRichRewrite = "            if (targetBotChat && securedPayload.rich_message) {\n                securedPayload.text = encryptEot10(JSON.stringify(securedPayload.rich_message));\n                delete securedPayload.rich_message;\n                securedMethod = method === \"sendRichMessageDraft\" ? \"sendMessageDraft\" : \"sendMessage\";\n            }";
                apiReplacement = apiReplacement.Replace(oldRichRewrite, "            if (targetBotChat && securedPayload.rich_message) securedPayload.rich_message = encryptEot10RichMessage(securedPayload.rich_message);");
                string mediaGroupGuard = "                if (targetBotChat && method === \"sendMediaGroup\") throw new Error(\"EOT10 media groups must be sent as individually encrypted documents\");";
                string mediaGroupEncryption = "                if (targetBotChat && method === \"sendMediaGroup\") {\n" +
                    "                    if (!Array.isArray(securedPayload.media) || securedPayload.media.length === 0) throw new Error(\"EOT10 media group is empty or malformed\");\n" +
                    "                    securedPayload.media = await Promise.all(securedPayload.media.map(async item => {\n" +
                    "                        const encryptedMedia = await encryptEot10MediaInput(item.media);\n" +
                    "                        const securedItem = { ...item, type: \"document\", media: new InputFile(encryptedMedia.buffer, encryptedMedia.filename) };\n" +
                    "                        delete securedItem.thumbnail; delete securedItem.thumb; delete securedItem.cover;\n" +
                    "                        if (typeof item.caption === \"string\") { securedItem.caption = encryptEot10(item.caption); delete securedItem.caption_entities; }\n" +
                    "                        return securedItem;\n" +
                    "                    }));\n" +
                    "                }";
                if (apiReplacement.IndexOf(mediaGroupGuard, StringComparison.Ordinal) < 0)
                {
                    Console.WriteLine("[加密] 无法安装 EOT10 媒体组加密适配。");
                    return false;
                }
                apiReplacement = apiReplacement.Replace(mediaGroupGuard, mediaGroupEncryption)
                    .Replace("sendVideoNote: \"video_note\" })[method]", "sendVideoNote: \"video_note\", sendSticker: \"sticker\" })[method]")
                    .Replace("delete securedPayload[mediaField];", "delete securedPayload[mediaField]; delete securedPayload.thumbnail; delete securedPayload.thumb; delete securedPayload.cover;");
                if (apiReplacement.IndexOf("EOT10 media group is empty or malformed", StringComparison.Ordinal) < 0 ||
                    apiReplacement.IndexOf("sendSticker: \"sticker\"", StringComparison.Ordinal) < 0 ||
                    apiReplacement.IndexOf("delete securedPayload.thumbnail", StringComparison.Ordinal) < 0)
                {
                    Console.WriteLine("[加密] 媒体 API 覆盖不完整，拒绝启用可能泄露附件的发送路径。");
                    return false;
                }
                if (apiReplacement.IndexOf("encryptMarkupText", StringComparison.Ordinal) >= 0 || apiReplacement.IndexOf("JSON.stringify(securedPayload.rich_message)", StringComparison.Ordinal) >= 0)
                {
                    Console.WriteLine("[加密] 无法安装富文本/按钮兼容补丁。");
                    return false;
                }
                if (content.Contains("\r\n")) { apiAnchor = apiAnchor.Replace("\n", "\r\n"); apiReplacement = apiReplacement.Replace("\n", "\r\n"); }
                 if (content.IndexOf(apiAnchor, StringComparison.Ordinal) < 0)
                 {
                     Console.WriteLine("[加密] 当前 Bot 版本的 Telegram API 调用结构已变化，未能定位加密插入点。");
                     return false;
                 }
                content = content.Replace(apiAnchor, apiReplacement);

                string middlewareAnchor = "    bot.use(authMiddleware);";
                  string middlewareReplacement = "    // EOT10 applies only to the authorized user's private conversation when explicitly enabled.\n    const eot10Enabled = process.env.OPENCODE_TELEGRAM_EOT10_ENABLED === \"1\" && Boolean(process.env.OPENCODE_TELEGRAM_EOT10_PASSWORD);\n    logger.info(`[EOT10 runtime environment status] desktop transport ${eot10Enabled ? \"enabled\" : \"disabled\"}`);\n    bot.use(async (ctx, next) => {\n        if (!eot10Enabled) { await next(); return; }\n        const message = ctx.message;\n        const authorizedSender = String(ctx.from?.id ?? \"\") === String(config.telegram.allowedUserId);\n        const targetBotPrivateChat = authorizedSender && ctx.chat?.type === \"private\" &&\n            String(ctx.chat.id) === String(config.telegram.allowedUserId);\n        if (authorizedSender && !targetBotPrivateChat) return;\n        if (!targetBotPrivateChat) { await next(); return; }\n        if (message && typeof message.text !== \"string\") {\n            await ctx.reply(\"v11 当前仅支持加密文本；媒体消息已拒绝。\");\n            return;\n        }\n        if (typeof message?.text === \"string\") {\n            if (!isEot10Message(message.text)) {\n                await ctx.reply(\"v11 加密模式已启用。请使用配套手机端发送 EOT10 加密消息。\");\n                return;\n            }\n            try { message.text = decryptEot10(message.text); }\n            catch (error) { logger.warn(\"[EOT10] Rejected invalid encrypted message\", error); await ctx.reply(\"加密消息无法验证或解密，请检查密码和消息完整性。\"); return; }\n        }\n        await next();\n    });\n    bot.use(authMiddleware);";
                string oldMediaGuard = "        if (message && typeof message.text !== \"string\") {\n            await ctx.reply(\"v11 当前仅支持加密文本；媒体消息已拒绝。\");\n            return;\n        }";
                string eot10MediaGuard = "        if (message && typeof message.text !== \"string\") {\n            const document = message.document;\n            if (!document || !isEot10MediaDocument(document)) {\n                await ctx.reply(\"加密模式只接受 EOT10 加密的图片/文档；其他媒体暂不支持。\");\n                return;\n            }\n            try {\n                const encryptedMedia = await downloadEot10TelegramFile(ctx.api, document.file_id);\n                const media = decryptEot10MediaBuffer(encryptedMedia.buffer);\n                cacheDecryptedEot10Media(document.file_id, { buffer: media.buffer, filePath: encryptedMedia.filePath });\n                document.file_name = media.metadata.filename;\n                document.mime_type = media.metadata.mimeType;\n                document.file_size = media.buffer.length;\n                if (message.caption) {\n                    if (!isEot10Message(message.caption)) { await ctx.reply(\"加密媒体说明文字必须使用 EOT10 加密。\"); return; }\n                    message.caption = decryptEot10(message.caption);\n                }\n            } catch (error) {\n                logger.warn(\"[EOT10] Rejected invalid encrypted media\", error);\n                await ctx.reply(\"加密文件无法验证或解密；请确认手机和 Bot 使用同一密码。\");\n                return;\n            }\n            await next();\n            return;\n        }";
                middlewareReplacement = middlewareReplacement.Replace("加密模式只接受 EOT10 加密的图片/文档；其他媒体暂不支持。", "加密模式只接受 EOT10 加密附件；请使用配套客户端重新发送。");
                middlewareReplacement = middlewareReplacement.Replace(oldMediaGuard, eot10MediaGuard);
                if (middlewareReplacement.IndexOf("v11 当前仅支持加密文本", StringComparison.Ordinal) >= 0)
                {
                    Console.WriteLine("[加密] 无法安装 EOT10 媒体接收适配。");
                    return false;
                }
                string commandEntityMiddleware =
                    "    // EOT10 command entity compatibility: rebuild slash-command metadata after decryption.\n" +
                    "    bot.use((ctx, next) => {\n" +
                    "        const message = ctx.message;\n" +
                    "        const match = typeof message?.text === \"string\" ? message.text.match(/^\\/[A-Za-z0-9_]+(?:@[A-Za-z0-9_]+)?(?=\\s|$)/) : null;\n" +
                    "        if (match) message.entities = [{ type: \"bot_command\", offset: 0, length: match[0].length }];\n" +
                    "        return next();\n" +
                    "    });\n";
                middlewareReplacement = middlewareReplacement.Replace("    bot.use(authMiddleware);", commandEntityMiddleware + "    bot.use(authMiddleware);");
                string disabledCiphertextGuard =
                    "    // EOT10 disabled-message guard: never pass encrypted input to OpenCode as plain text.\n" +
                    "    bot.use(async (ctx, next) => {\n" +
                    "        const enabled = process.env.OPENCODE_TELEGRAM_EOT10_ENABLED === \"1\" && Boolean(process.env.OPENCODE_TELEGRAM_EOT10_PASSWORD);\n" +
                    "        const authorized = String(ctx.from?.id ?? \"\") === String(config.telegram.allowedUserId);\n" +
                    "        const target = authorized && ctx.chat?.type === \"private\" && String(ctx.chat.id) === String(config.telegram.allowedUserId);\n" +
                    "        if (!enabled && target && (isEot10Message(ctx.message?.text) || isEot10MediaDocument(ctx.message?.document))) { await ctx.reply(\"桌面端 EOT10 已关闭，请在启动器开启加密后重试，或发送普通文本/普通文件。\"); return; }\n" +
                    "        await next();\n" +
                    "    });\n";
                middlewareReplacement = middlewareReplacement.Replace("    // EOT10 applies only to the authorized user's private conversation when explicitly enabled.", disabledCiphertextGuard + "    // EOT10 applies only to the authorized user's private conversation when explicitly enabled.");
                if (content.Contains("\r\n")) { middlewareAnchor = middlewareAnchor.Replace("\n", "\r\n"); middlewareReplacement = middlewareReplacement.Replace("\n", "\r\n"); }
                 if (content.IndexOf(middlewareAnchor, StringComparison.Ordinal) < 0)
                 {
                     Console.WriteLine("[加密] 当前 Bot 版本的中间件结构已变化，未能定位解密插入点。");
                     return false;
                 }
                content = content.Replace(middlewareAnchor, middlewareReplacement);

                string backup = botIndex + ".eot10-original";
                // Refresh from the exact clean package version before applying our runtime patch.
                File.Copy(botIndex, backup, true);
                File.WriteAllText(botIndex, content, new UTF8Encoding(false));
            }
            Console.WriteLine("[加密] EOT10 AES-256-GCM 收发适配已启用。");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[加密] 无法启用 EOT10 加密适配：" + ex.Message);
            return false;
        }
    }

    static bool EnsureEot10Disabled(string appData)
    {
        Environment.SetEnvironmentVariable("OPENCODE_TELEGRAM_EOT10_ENABLED", "0", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("OPENCODE_TELEGRAM_EOT10_PASSWORD", null, EnvironmentVariableTarget.Process);
        try
        {
            string botIndex = InstalledBotEntryPath(appData);
            if (File.Exists(botIndex))
            {
                string content = File.ReadAllText(botIndex, Encoding.UTF8);
                if (content.IndexOf("EOT10 desktop transport", StringComparison.Ordinal) >= 0 &&
                    content.IndexOf("OPENCODE_TELEGRAM_EOT10_ENABLED", StringComparison.Ordinal) < 0)
                {
                    string backup = botIndex + ".eot10-original";
                    if (!File.Exists(backup))
                    {
                        Console.WriteLine("[加密] 发现旧版强制加密补丁，未找到原始备份；为避免误处理，Bot 尚未启动。");
                        return false;
                    }
                    File.Copy(backup, botIndex, true);
                    Console.WriteLine("[加密] 已移除旧版强制加密补丁，恢复普通 Bot 行为。");
                }
            }
            Console.WriteLine("[加密] EOT10 已关闭；Bot 按普通模式运行。");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[加密] 关闭旧版 EOT10 补丁失败：" + ex.Message);
            return false;
        }
    }

    static string ReadPasswordMasked()
    {
        StringBuilder password = new StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0) password.Length--;
            }
            else if (!Char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
        }
        Console.WriteLine();
        return password.ToString();
    }

    static string Eot11SettingsPath(string appData)
    {
        return Path.Combine(appData, "opencode-telegram-bot", "eot11-settings.dpapi");
    }

    static bool SaveEot11Settings(string appData)
    {
        try
        {
            string path = Eot11SettingsPath(appData);
            string directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            byte[] passwordBytes = Encoding.UTF8.GetBytes(eot10Password ?? "");
            byte[] payload = new byte[passwordBytes.Length + 1];
            payload[0] = eot10Enabled ? (byte)1 : (byte)0;
            Buffer.BlockCopy(passwordBytes, 0, payload, 1, passwordBytes.Length);
            byte[] entropy = Encoding.UTF8.GetBytes("OpenCodeTelegram-v11-settings");
            byte[] protectedBytes = ProtectedData.Protect(payload, entropy, DataProtectionScope.CurrentUser);
            string tempPath = path + ".tmp";
            File.WriteAllBytes(tempPath, protectedBytes);
            File.Copy(tempPath, path, true);
            File.Delete(tempPath);
            Array.Clear(passwordBytes, 0, passwordBytes.Length);
            Array.Clear(payload, 0, payload.Length);
            Array.Clear(protectedBytes, 0, protectedBytes.Length);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[加密] 无法保存 v11 配置：" + ex.Message);
            return false;
        }
    }

    static bool LoadEot11Settings(string appData)
    {
        string path = Eot11SettingsPath(appData);
        try
        {
            if (File.Exists(path))
            {
                byte[] entropy = Encoding.UTF8.GetBytes("OpenCodeTelegram-v11-settings");
                byte[] payload = ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.CurrentUser);
                if (payload.Length < 1) throw new CryptographicException("empty settings");
                eot10Enabled = payload[0] == 1;
                eot10Password = Encoding.UTF8.GetString(payload, 1, payload.Length - 1);
                Array.Clear(payload, 0, payload.Length);
                if (eot10Enabled && eot10Password.Length < 12) throw new CryptographicException("saved password is invalid");
                return true;
            }

            // Preserve an existing v10 password, but keep v11 encryption opt-in by default.
            string legacyPath = Path.Combine(appData, "opencode-telegram-bot", "eot10-password.dpapi");
            if (File.Exists(legacyPath))
            {
                byte[] entropy = Encoding.UTF8.GetBytes("OpenCodeTelegram-v10-password");
                byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(legacyPath), entropy, DataProtectionScope.CurrentUser);
                string legacyPassword = Encoding.UTF8.GetString(clear);
                Array.Clear(clear, 0, clear.Length);
                if (legacyPassword.Length >= 12)
                {
                    eot10Password = legacyPassword;
                    eot10Enabled = false;
                    eot10MigratedLegacyPassword = SaveEot11Settings(appData);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            eot10Enabled = false;
            eot10Password = "";
            Console.WriteLine("[加密] 无法读取保存的 v11 配置，默认保持关闭：" + ex.Message);
            return false;
        }
    }

    static bool SetEot11Password()
    {
        Console.WriteLine("设置 EOT10 密码（至少 12 个字符；手机端对应 Bot 使用相同密码）");
        Console.Write("请输入新密码：");
        string first = ReadPasswordMasked();
        Console.Write("请再次输入：");
        string second = ReadPasswordMasked();
        if (first.Length < 12 || !String.Equals(first, second, StringComparison.Ordinal))
        {
            Console.WriteLine("[加密] 密码长度不足或两次输入不一致，未更改配置。");
            return false;
        }
        bool previousEnabled = eot10Enabled;
        string previousPassword = eot10Password;
        eot10Password = first;
        eot10Enabled = true;
        if (!SaveEot11Settings(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)))
        {
            eot10Enabled = previousEnabled;
            eot10Password = previousPassword;
            return false;
        }
        Console.WriteLine("[加密] 密码已通过当前 Windows 用户 DPAPI 加密保存，EOT10 已开启。");
        Console.WriteLine("提示：更改密码后，旧密码加密的历史消息仍需要旧密码解密。");
        return true;
    }

    static void ConfigureEot11AtStartup(string appData)
    {
        Console.WriteLine();
        Console.WriteLine("EOT10 加密当前状态：" + (eot10Enabled ? "开启" : "关闭（普通模式）"));
        if (eot10MigratedLegacyPassword && !eot10Enabled)
            Console.WriteLine("已保留 v10 密码；如要使用，请在设置中选择开启。");
        Console.WriteLine("启动后 10 秒内按 E 配置加密；超时将自动继续，并加载已保存设置。");
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (stopping) return;
            try
            {
                if (Console.KeyAvailable)
                {
                    ConsoleKeyInfo key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.E)
                    {
                        ShowEot11SettingsMenu(appData);
                        return;
                    }
                }
            }
            catch { return; }
            Thread.Sleep(100);
        }
        Console.WriteLine("[加密] 设置等待结束，继续启动。");
    }

    static void ShowEot11SettingsMenu(string appData)
    {
        Console.WriteLine();
        Console.WriteLine("========== v11 加密设置 ==========");
        Console.WriteLine("1. 开启加密（使用已保存密码）");
        Console.WriteLine("2. 添加/修改密码并开启加密");
        Console.WriteLine("3. 关闭加密（保留密码）");
        Console.WriteLine("4. 删除已保存密码并关闭加密");
        Console.WriteLine("0. 不更改，继续启动");
        Console.Write("请选择：");
        ConsoleKey choice = Console.ReadKey(true).Key;
        Console.WriteLine();
        switch (choice)
        {
            case ConsoleKey.D1:
            case ConsoleKey.NumPad1:
                if (eot10Password.Length < 12) Console.WriteLine("[加密] 当前没有有效的已保存密码，请选择 2 添加密码。");
                else
                {
                    bool previousEnabled = eot10Enabled;
                    eot10Enabled = true;
                    if (SaveEot11Settings(appData)) Console.WriteLine("[加密] 已开启，并保存设置。");
                    else eot10Enabled = previousEnabled;
                }
                break;
            case ConsoleKey.D2:
            case ConsoleKey.NumPad2:
                SetEot11Password();
                break;
            case ConsoleKey.D3:
            case ConsoleKey.NumPad3:
                bool wasEnabled = eot10Enabled;
                eot10Enabled = false;
                if (SaveEot11Settings(appData)) Console.WriteLine("[加密] 已关闭；密码仍保存在本机。");
                else eot10Enabled = wasEnabled;
                break;
            case ConsoleKey.D4:
            case ConsoleKey.NumPad4:
                bool wasEnabledBeforeDelete = eot10Enabled;
                string savedPasswordBeforeDelete = eot10Password;
                eot10Enabled = false;
                eot10Password = "";
                if (SaveEot11Settings(appData)) Console.WriteLine("[加密] 已关闭并删除保存的密码。");
                else { eot10Enabled = wasEnabledBeforeDelete; eot10Password = savedPasswordBeforeDelete; }
                break;
            default:
                Console.WriteLine("[加密] 保持现有设置。");
                break;
        }
    }

    static void ApplyEot11RuntimeSettings()
    {
        Environment.SetEnvironmentVariable("OPENCODE_TELEGRAM_EOT10_ENABLED", eot10Enabled ? "1" : "0", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("OPENCODE_TELEGRAM_EOT10_PASSWORD", eot10Enabled ? eot10Password : null, EnvironmentVariableTarget.Process);
    }

    static void ChangeEot10Password(string appData)
    {
        LoadEot11Settings(appData);
        ShowEot11SettingsMenu(appData);
        Console.WriteLine("按任意键退出。");
        Console.ReadKey(true);
    }

    static bool EnsureNewSessionClearsStaleQuestion(string appData)
    {
        try
        {
            string dist = Path.Combine(appData, "npm", "node_modules", "@grinev", "opencode-telegram-bot", "dist");
            string guardPath = Path.Combine(dist, "bot", "middleware", "interaction-guard-decision.js");
            string guardReplacement = "if (command === \"/start\") {\n            return createAllowDecision(inputType, state, command);\n        }\n        // Allow /new to reach the existing session-creation cleanup only when idle.\n        if (command === \"/new\" && state.kind === \"question\") {\n            return createAllowDecision(inputType, state, command);\n        }";
            bool guardReady = PatchInstalledFile(guardPath,
                "command === \"/new\" && state.kind === \"question\"",
                new Regex("if \\(command === \\\"/start\\\"\\) \\{\\s*return createAllowDecision\\(inputType, state, command\\);\\s*\\}"),
                guardReplacement);

            string newCommandPath = Path.Combine(dist, "bot", "commands", "new-command.js");
            bool newCommandReady = false;
            if (File.Exists(newCommandPath))
            {
                string content = File.ReadAllText(newCommandPath, Encoding.UTF8);
                if (content.IndexOf("import { questionManager }", StringComparison.Ordinal) < 0)
                {
                    Regex importPattern = new Regex("import \\{ clearAllInteractionState \\} from \\\"\\.\\./\\.\\./app/managers/interaction-manager\\.js\\\";");
                    Match importMatch = importPattern.Match(content);
                    if (importMatch.Success)
                    {
                        string newline = content.Contains("\r\n") ? "\r\n" : "\n";
                        string importReplacement = importMatch.Value + newline + "import { questionManager } from \"../../app/managers/question-manager.js\";";
                        content = importPattern.Replace(content, importReplacement, 1);
                        string backupPath = newCommandPath + ".codex-original";
                        if (!File.Exists(backupPath)) File.Copy(newCommandPath, backupPath, false);
                        File.WriteAllText(newCommandPath, content, new UTF8Encoding(false));
                    }
                }
                content = File.ReadAllText(newCommandPath, Encoding.UTF8);
                string clearReplacement = "setCurrentSession(sessionInfo);\n        if (questionManager.isActive()) {\n            if (ctx.chat) {\n                for (const messageId of questionManager.getMessageIds()) {\n                    await ctx.api.deleteMessage(ctx.chat.id, messageId).catch(() => {});\n                }\n            }\n            questionManager.clear();\n        }\n        clearAllInteractionState(\"session_created\");";
                newCommandReady = PatchInstalledFile(newCommandPath,
                    "questionManager.getMessageIds()",
                    new Regex("setCurrentSession\\(sessionInfo\\);\\s*clearAllInteractionState\\(\\\"session_created\\\"\\);"),
                    clearReplacement);
                newCommandReady = newCommandReady && content.IndexOf("import { questionManager }", StringComparison.Ordinal) >= 0;
            }

            if (guardReady && newCommandReady)
            {
                Console.WriteLine("[修复] 已启用 /new 自动清理旧问答弹窗（仅空闲时生效）。");
                return true;
            }
            Console.WriteLine("[修复] 当前 Bot 文件结构与补丁预期不一致；未完整应用新会话清理修复。");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[修复] 无法应用新会话清理修复：" + ex.Message);
            return false;
        }
    }

    static string RunNpm(string arguments, int timeoutMs, out int exitCode, out string error)
    {
        exitCode = -1;
        error = "";
        try
        {
            using (var p = Process.Start(new ProcessStartInfo {
                FileName = "cmd.exe",
                Arguments = "/d /c npm " + arguments,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }))
            {
                StringBuilder output = new StringBuilder();
                StringBuilder errorOutput = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errorOutput) errorOutput.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { Process.Start(new ProcessStartInfo { FileName = "taskkill.exe", Arguments = "/PID " + p.Id + " /T /F", UseShellExecute = false, CreateNoWindow = true }).WaitForExit(5000); } catch { }
                    exitCode = -2;
                    lock (output) lock (errorOutput) { error = errorOutput.ToString(); return output.ToString(); }
                }
                p.WaitForExit(); // Flush async output events after process exit.
                exitCode = p.ExitCode;
                lock (output) lock (errorOutput) { error = errorOutput.ToString(); return output.ToString(); }
            }
        }
        catch (Exception ex) { error = ex.Message; return ""; }
    }

    static void StopExistingBotForUpdate(string botCmd)
    {
        try
        {
            using (var stop = Process.Start(new ProcessStartInfo {
                FileName = "cmd.exe", Arguments = "/d /c \"\"" + botCmd + "\" stop\"",
                UseShellExecute = false, CreateNoWindow = true })) { stop.WaitForExit(15000); }
            System.Threading.Thread.Sleep(1500);
        }
        catch { }
    }

    static void TryUpdateBot(string botCmd)
    {
        Console.WriteLine("[1/5] 正在检查 OpenCode Telegram Bot 更新（失败也会继续启动）...");
        try
        {
            int installedCode, latestCode;
            string installedError, latestError;
            string installedText = RunNpm("list -g @grinev/opencode-telegram-bot --depth=0", 30000, out installedCode, out installedError);
            string latestText = RunNpm("view @grinev/opencode-telegram-bot version --silent", 30000, out latestCode, out latestError);
            Match installed = Regex.Match(installedText, @"@grinev/opencode-telegram-bot@([0-9]+\.[0-9]+\.[0-9]+)");
            Match latest = Regex.Match(latestText, @"([0-9]+\.[0-9]+\.[0-9]+)");
            if (installedCode == 0 && latestCode == 0 && installed.Success && latest.Success && installed.Groups[1].Value == latest.Groups[1].Value)
            {
                Console.WriteLine("[2/5] 已是最新版 " + installed.Groups[1].Value + "，无需更新。");
                Console.WriteLine("[3/5] 使用当前版本启动。");
                return;
            }
            if (installedCode != 0 || latestCode != 0 || !installed.Success || !latest.Success)
            {
                Console.WriteLine("[2/5] 无法确认 npm 最新版本，保留当前版本。");
                if (!String.IsNullOrWhiteSpace(latestError)) Console.WriteLine("npm 错误：" + latestError.Trim());
                Console.WriteLine("[3/5] 使用当前版本启动。");
                return;
            }

            StopExistingBotForUpdate(botCmd);
            Console.WriteLine("[2/5] 正在更新程序，请稍候...");
            int updateCode; string updateError;
            RunNpm("install --loglevel=error --global @grinev/opencode-telegram-bot@latest", 300000, out updateCode, out updateError);
            if (updateCode == -2) Console.WriteLine("[3/5] 更新超时，启用当前已安装版本并继续启动。");
            else if (updateCode == 0) Console.WriteLine("[3/5] 更新完成，准备启动最新版本。");
            else { Console.WriteLine("[3/5] 更新失败（退出码 " + updateCode + "），启用当前已安装版本。"); if (!String.IsNullOrWhiteSpace(updateError)) Console.WriteLine("npm 错误：" + updateError.Trim()); }
        }
        catch (Exception ex) { Console.WriteLine("[3/5] 更新检查无法执行，启用当前已安装版本：" + ex.Message); }
    }

    [DllImport("Kernel32")]
    static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandlerRoutine handler, bool add);
    delegate bool ConsoleCtrlHandlerRoutine(uint signal);

    static bool HandleConsoleSignal(uint signal)
    {
        if (signal == 0 || signal == 2 || signal == 5 || signal == 6 || signal == 15)
        {
            StopServices();
            return true;
        }
        return false;
    }

    static void Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        if (args != null && Array.Exists(args, arg => String.Equals(arg, "--verify-portable", StringComparison.OrdinalIgnoreCase)))
        {
            if (String.IsNullOrEmpty(ReadEot10CryptoSource()))
            {
                Console.Error.WriteLine("Embedded EOT10 module is missing.");
                Environment.ExitCode = 1;
            }
            else
            {
                Console.WriteLine("Embedded EOT10 module loaded independently of the EXE directory.");
            }
            return;
        }
        singleInstanceMutex = new Mutex(true, "Local\\OpenCodeTelegramLauncher.SingleInstance", out ownsMutex);
        if (!ownsMutex)
        {
            Console.WriteLine("启动器已经在运行，当前窗口不会重复启动服务。");
            System.Threading.Thread.Sleep(2500);
            return;
        }
        Console.Title = "OpenCode Telegram 控制台（关闭窗口即可停止服务）";
        SetConsoleCtrlHandler(HandleConsoleSignal, true);
        AppDomain.CurrentDomain.ProcessExit += (s, e) => StopServices();

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (args != null && Array.Exists(args, arg => String.Equals(arg, "--change-password", StringComparison.OrdinalIgnoreCase)))
        {
            ChangeEot10Password(appData);
            return;
        }
        string botCmd = Path.Combine(appData, "npm", "opencode-telegram.cmd");
        if (!File.Exists(botCmd))
        {
            Console.WriteLine("找不到 OpenCode Telegram Bot：" + botCmd);
            Console.WriteLine("请先安装 @grinev/opencode-telegram-bot。");
            Console.ReadKey();
            return;
        }

        LoadEot11Settings(appData);
        ConfigureEot11AtStartup(appData);
        if (stopping) return;
        ApplyEot11RuntimeSettings();
        bool skipUpdate = args != null && Array.Exists(args, arg => String.Equals(arg, "--skip-update", StringComparison.OrdinalIgnoreCase));
        if (skipUpdate) Console.WriteLine("[更新] 已按参数跳过 npm 更新，使用当前已安装版本。");
        else TryUpdateBot(botCmd);
        if (!EnsureInstalledBotEntryAvailable(appData)) { Console.ReadKey(); return; }
        if (!EnsureGenericDocumentForwarding(appData)) { Console.ReadKey(); return; }
        Console.WriteLine("[4/7] 正在应用 EOT10 收发设置...");
        bool eotReady = eot10Enabled ? EnsureEot10Encryption(appData) : EnsureEot10Disabled(appData);
        if (!eotReady) { Console.ReadKey(); return; }
        Console.WriteLine("[加密] 当前运行模式：" + (eot10Enabled ? "EOT10 已开启" : "普通模式（不加密）"));
        Console.WriteLine("[5/7] 正在检查新会话交互修复...");
        EnsureNewSessionClearsStaleQuestion(appData);
        Console.WriteLine("[Bot] 正在停止旧 daemon，以便新进程加载当前 EOT10 配置...");
        StopExistingBotForUpdate(botCmd);
        Console.WriteLine("[6/7] 正在检查 OpenCode Server...");
        string serverExe = Path.Combine(appData, "npm", "node_modules", "opencode-ai", "bin", "opencode.exe");
        if (!File.Exists(serverExe)) { Console.WriteLine("找不到 OpenCode：" + serverExe); Console.ReadKey(); return; }
        if (IsOpenCodeServerHealthy())
            Console.WriteLine("OpenCode Server 已在 127.0.0.1:4096 运行，复用现有服务。");
        else try
        {
            server = Process.Start(new ProcessStartInfo { FileName = serverExe, Arguments = "serve --port 4096", WorkingDirectory = appData, UseShellExecute = false, CreateNoWindow = true });
            System.Threading.Thread.Sleep(2000);
        }
        catch (Exception ex) { Console.WriteLine("OpenCode 启动失败：" + ex.Message); Console.ReadKey(); return; }
        Console.WriteLine("[7/7] 正在启动 Telegram 机器人...");
        bot = new Process();
        bot.StartInfo = new ProcessStartInfo {
            FileName = "cmd.exe",
            Arguments = "/d /c \"\"" + botCmd + "\" start --daemon --mode installed\"",
            WorkingDirectory = appData,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        lastBotStartAttemptUtc = DateTime.UtcNow;
        try { bot.Start(); }
        catch (Exception ex) { Console.WriteLine("启动失败：" + ex.Message); Console.ReadKey(); return; }

        System.Threading.Thread.Sleep(2500);
        Console.WriteLine("机器人已经启动，可以进行对话。OpenCode Server 已监听 127.0.0.1:4096。");
        Console.WriteLine("Bot 意外退出会自动检测并尝试恢复，每 20 秒检查一次。");
        Console.WriteLine("保持此窗口打开；关闭窗口将停止机器人和 OpenCode。\n");
        bool firstProbe = true;
        while (!stopping)
        {
            Thread.Sleep(20000);
            if (stopping) break;
            // The launcher may not use the Bot's Telegram proxy. A separate getMe
            // probe can fail while the daemon is successfully polling and replying.
            if (!IsBotDaemonRunning(botCmd))
            {
                RestartBotDaemon(botCmd, firstProbe ? "Bot daemon 未运行" : "检测到 Bot daemon 已退出");
            }
            firstProbe = false;
        }
        try { if (ownsMutex) singleInstanceMutex.ReleaseMutex(); } catch { }
    }

    static void StopServices()
    {
        if (stopping) return;
        stopping = true;
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string botCmd = Path.Combine(appData, "npm", "opencode-telegram.cmd");
            if (File.Exists(botCmd))
            {
                using (var p = Process.Start(new ProcessStartInfo {
                    FileName = "cmd.exe", Arguments = "/d /c \"\"" + botCmd + "\" stop\"",
                    UseShellExecute = false, CreateNoWindow = true })) { p.WaitForExit(10000); }
            }
        }
        catch { }
        try { if (bot != null && !bot.HasExited) Process.Start(new ProcessStartInfo { FileName = "taskkill.exe", Arguments = "/PID " + bot.Id + " /T /F", UseShellExecute = false, CreateNoWindow = true }).WaitForExit(5000); } catch { }
        try { if (server != null && !server.HasExited) Process.Start(new ProcessStartInfo { FileName = "taskkill.exe", Arguments = "/PID " + server.Id + " /T /F", UseShellExecute = false, CreateNoWindow = true }).WaitForExit(5000); } catch { }
        try
        {
            // OpenCode is started detached by the bot; explicitly stop the listener.
            using (var net = Process.Start(new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c netstat -ano", UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true }))
            {
                string output = net.StandardOutput.ReadToEnd();
                net.WaitForExit(3000);
                foreach (Match m in Regex.Matches(output, @"127\.0\.0\.1:4096\s+\S+\s+\S+\s+(\d+)", RegexOptions.IgnoreCase))
                {
                    Process.Start(new ProcessStartInfo { FileName = "taskkill.exe", Arguments = "/PID " + m.Groups[1].Value + " /T /F", UseShellExecute = false, CreateNoWindow = true }).WaitForExit(5000);
                }
            }
        }
        catch { }
    }
}
