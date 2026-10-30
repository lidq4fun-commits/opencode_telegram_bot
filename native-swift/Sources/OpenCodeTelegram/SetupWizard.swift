import SwiftUI

enum SetupStep: Int, CaseIterable { case telegram, bot, encryption, engine }

enum SetupRules {
    static func validBotFields(token: String, userID: String) -> Bool {
        token.range(of: "\\A[0-9]+:[A-Za-z0-9_-]+\\z", options: .regularExpression) != nil &&
            userID.allSatisfy { $0.isASCII && $0.isNumber } && (Int64(userID) ?? 0) > 0
    }
    static func encryptionReady(_ settings: Settings) -> Bool {
        !settings.encryptionEnabled || settings.encryptionPassword.count >= 12
    }
    static func botConfigured(_ settings: Settings) -> Bool {
        validBotFields(token: settings.botToken, userID: settings.allowedUserID)
    }
    static func shouldStartBot(_ settings: Settings, running: Bool) -> Bool {
        botConfigured(settings) && !running
    }
    static func engineChoices(_ candidates: [EngineCandidate], resources: URL = Runtime.resources) -> [EngineCandidate] {
        candidates.sorted { first, second in
            if first.isBundled(resources: resources) != second.isBundled(resources: resources) { return first.isBundled(resources: resources) }
            return first.path < second.path
        }
    }
    static func completed(defaults: UserDefaults = .standard) -> Bool {
        defaults.bool(forKey: "setupWizardCompleted")
    }
    static func markCompleted(defaults: UserDefaults = .standard) {
        defaults.set(true, forKey: "setupWizardCompleted")
    }
}

struct VerifiedBot { let username: String }

enum BotBindingVerifier {
    // Raw networking errors may contain the credential-bearing URL: never surface them.
    static func verify(token: String, userID: String,
        transport: (URLRequest) async throws -> (Data, URLResponse) = request) async throws -> VerifiedBot {
        guard SetupRules.validBotFields(token: token, userID: userID) else {
            throw AppFailure.message("Bot Token / User ID 格式无效。 / Invalid Bot Token / User ID format.")
        }
        func call(_ method: String, body: [String: String] = [:]) async throws -> [String: Any] {
            var request = URLRequest(url: URL(string: "https://api.telegram.org/bot\(token)/\(method)")!, timeoutInterval: 20)
            request.httpMethod = "POST"
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            let data: Data, response: URLResponse
            do { (data, response) = try await transport(request) }
            catch { throw AppFailure.message("无法连接 Telegram，请检查网络后重试。 / Could not reach Telegram; check connectivity and retry.") }
            guard (response as? HTTPURLResponse)?.statusCode == 200,
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  json["ok"] as? Bool == true, let result = json["result"] as? [String: Any] else {
                throw AppFailure.message(method == "getMe"
                    ? "Bot Token 验证失败，请检查 Token。 / Bot Token verification failed; check the token."
                    : "请先在手机打开该 Bot 并发送 /start，再验证绑定。 / Open this Bot on your phone and send /start, then verify again.")
            }
            return result
        }
        let bot = try await call("getMe")
        guard bot["is_bot"] as? Bool == true, let username = bot["username"] as? String else {
            throw AppFailure.message("返回的账号不是有效 Bot。 / The account is not a valid Bot.")
        }
        let chat = try await call("getChat", body: ["chat_id": userID])
        guard chat["type"] as? String == "private", (chat["id"] as? NSNumber)?.int64Value == Int64(userID) else {
            throw AppFailure.message("允许的 User ID 必须对应个人私聊。 / The allowed User ID must identify a private user chat.")
        }
        return VerifiedBot(username: username)
    }
    private static func request(_ request: URLRequest) async throws -> (Data, URLResponse) {
        let session = URLSession(configuration: .ephemeral)
        defer { session.invalidateAndCancel() }
        return try await session.data(for: request)
    }
}

struct SetupWizardView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var telegram: TelegramClient
    init(model: AppModel) { self.model = model; telegram = model.telegram }

    private var titles: [String] {
        [model.text("tg 登录", "tg Login"), model.text("Bot 绑定", "Bot Binding"),
         model.text("加密设置", "Encryption"), model.text("选择 OpenCode", "Choose OpenCode")]
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack {
                Text(model.text("首次使用向导", "First-run setup")).font(.largeTitle).fontWeight(.semibold)
                Spacer()
                Picker("Language", selection: $model.settings.locale) { Text("简体中文").tag("zh"); Text("English").tag("en") }.frame(width: 200)
            }
            HStack(spacing: 16) {
                ForEach(SetupStep.allCases, id: \.rawValue) { step in
                    Text("\(step.rawValue + 1). \(titles[step.rawValue])")
                        .foregroundStyle(step == model.setupStep ? Color.blue : Color.secondary)
                        .fontWeight(step == model.setupStep ? .bold : .regular)
                }
            }
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    switch model.setupStep {
                    case .telegram:
                        Text(model.text("这一步做什么：把你自己的 Telegram 账号登录到本机，不需要 Bot Token，也不用在终端执行命令。请先在手机 Telegram 登录自己的账号，并保持手机和电脑联网。\n\n1. 点击下面的「扫码登录」，等待二维码出现。\n2. 在手机 Telegram 打开「设置 → 设备 → 连接桌面设备」，用该页面的扫码功能扫描电脑上的二维码，不要使用普通相机。\n3. 若提示双重验证，在下面的安全输入框填写你在 Telegram 设置中设置的两步验证密码。这不是短信验证码，也不是后面要设置的加密密码。\n4. 当页面显示你的账号名称 / @用户名且「继续」可点击时，登录完成，点击继续。\n\n二维码过期会自动更新；登录失败请检查手机和电脑网络，取消后重新扫码。已经登录的账号会自动识别，无需再次扫码。不要分享二维码或密码。", "Purpose: sign your personal Telegram account into this computer. No Bot Token or terminal commands are needed. First sign into Telegram on your phone and keep both devices online.\n\n1. Click Log in with QR below and wait for the code.\n2. On your phone, open Telegram Settings → Devices → Link Desktop Device and scan the computer's QR using that scanner, not the normal camera.\n3. If two-step verification is requested, enter the password you set in Telegram in the secure field below. This is not an SMS code or the encryption passphrase configured later.\n4. When your account name / @username appears and Continue becomes available, click Continue.\n\nExpired QR codes refresh automatically. If login fails, check connectivity, cancel and scan again. An existing login is detected automatically. Never share the QR or password."))
                        TelegramClientView(client: telegram, english: model.settings.locale == "en", loginOnly: true)
                    case .bot:
                        Text(model.text("这一步做什么：创建用于远程控制 OpenCode 的机器人，并指定只有哪个 Telegram 用户能控制它。\n\n1. 点击下面的官方 @BotFather 链接，在 Telegram 中点击 Start，发送 /newbot。先输入机器人的显示名称，再输入唯一用户名；用户名必须以 bot 结尾，例如 my_opencode_bot。\n2. 创建成功后，BotFather 会给出 HTTP API Token（形式类似「一串数字:一串字母」）。复制完整 Token，粘贴到下面的「Bot Token」，不要填写手机号、@用户名或 Telegram 登录验证码。已有机器人可发送 /mybots，选择该机器人 → API Token 获取。\n3. 在手机 Telegram 搜索刚创建的 @机器人用户名，打开聊天，点击 Start 或发送 /start。没有这一步，机器人不能主动联系你，绑定验证也会失败。\n4. 「允许的 Telegram User ID」已自动填写上一步登录账号的数字 ID，使用同一账号控制时不要改。它不是手机号、机器人 ID 或 @用户名。若要允许另一账号控制，请用那个账号打开下面的 @userinfobot，点击 Start，将回复的 Id 数字填入此处，并用那个账号向你的机器人发送 /start。\n5. 点击「验证并保存绑定」。出现绿色「绑定已验证」后，点击继续。验证失败时检查完整 Token、User ID 和手机的 /start，再重试。\n\n@userinfobot 是第三方查询机器人，只用来查询数字 ID；不要向它发送 Token、密码或验证码。", "Purpose: create the Bot that remotely controls OpenCode and specify the single Telegram user allowed to control it.\n\n1. Open the official @BotFather link below, tap Start and send /newbot. Enter a display name, then a unique username ending in bot, such as my_opencode_bot.\n2. BotFather returns an HTTP API Token (digits, a colon, then letters). Copy the entire token into Bot Token below. Do not enter a phone number, @username or Telegram login code. For an existing Bot, send /mybots, select it and choose API Token.\n3. On your phone, find your new @bot_username, open the chat and tap Start or send /start. Without this, the Bot cannot contact you and verification will fail.\n4. Allowed Telegram User ID defaults to the numeric ID of the account signed in above; leave it unchanged for that account. It is not a phone number, Bot ID or @username. To allow a different account, use that account to open @userinfobot below, tap Start, copy its numeric Id here and send /start to your Bot from that account.\n5. Click Verify and save binding. When the green Binding verified label appears, click Continue. If verification fails, check the complete token, user ID and /start, then retry.\n\n@userinfobot is a third-party numeric-ID lookup Bot. Never send it a token, password or login code."))
                        Link(model.text("打开官方 @BotFather 创建 / 管理机器人", "Open official @BotFather to create / manage your Bot"), destination: URL(string: "https://t.me/BotFather")!)
                        Link(model.text("用控制账号打开 @userinfobot 查询数字 ID（第三方）", "Open @userinfobot with the controlling account to look up its numeric ID (third-party)"), destination: URL(string: "https://t.me/userinfobot")!)
                        SecureField("Bot Token", text: $model.settings.botToken)
                        TextField(model.text("允许的 Telegram User ID", "Allowed Telegram User ID"), text: $model.settings.allowedUserID)
                        Button(model.text("验证并保存绑定", "Verify and save binding")) { model.verifySetupBot() }
                            .buttonStyle(.borderedProminent).disabled(model.busy || !SetupRules.botConfigured(model.settings))
                        if model.setupBindingVerified {
                            Label("@" + model.setupBotName + model.text(" · 绑定已验证", " · Binding verified"), systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                        }
                        Text(model.text("Token 存储在 macOS 钥匙串，不写入日志。", "The token is stored in macOS Keychain, not in logs.")).font(.caption)
                    case .encryption:
                        Text(model.text("这一步做什么：选择是否对你与 Bot 私聊的内容额外加密。\n\n推荐首次使用：保持下面的加密开关关闭，直接点击继续。普通 Telegram 客户端就能使用，不需要额外下载或填写密码。\n\n若你确实需要 EOT10 加密：\n1. 点击下面的兼容安卓客户端链接，先阅读说明，再下载安装其中的特殊加密版 Telegram。官方 Telegram 不支持此协议。\n2. 在该安卓客户端进入 Settings → Bot Encryption，添加上一步创建的 Bot 用户名，并自行设置至少 12 个字符的密码。这个密码由你自己创建，不从 BotFather 获取。\n3. 打开电脑下面的加密开关，填写完全相同的密码，检查后点击继续。两端密码不一致会导致消息或附件无法解密。\n\n这里的密码不是 tg 登录的两步验证密码。EOT10 是 Bot 私聊的应用层加密，不是 Telegram Secret Chat；本机 tg 监听页面不会自动解密它。以后可在侧栏「加密设置」修改。", "Purpose: optionally add application-layer encryption to your private Bot conversation.\n\nRecommended for first use: leave encryption off and click Continue. Standard Telegram works without an extra download or passphrase.\n\nIf you need EOT10 encryption:\n1. Open the compatible Android client link below, read its notes and install the special encrypted Telegram build. Official Telegram does not support this protocol.\n2. In that Android client, open Settings → Bot Encryption, add the Bot username created above and choose your own passphrase of at least 12 characters. You create this passphrase; BotFather does not provide it.\n3. Enable encryption below, enter exactly the same passphrase on this computer and click Continue. Different passphrases prevent messages and attachments from decrypting.\n\nThis is not the tg two-step verification password. EOT10 covers the private Bot chat and is not Telegram Secret Chat; the local tg listener does not decrypt it. You can change it later in Encryption settings."))
                        Toggle(model.text("启用 EOT10 加密", "Enable EOT10 encryption"), isOn: $model.settings.encryptionEnabled)
                        SecureField(model.text("加密密码（至少 12 个字符）", "Encryption passphrase (12+ characters)"), text: $model.settings.encryptionPassword).disabled(!model.settings.encryptionEnabled)
                        Link(model.text("查看兼容安卓客户端与说明", "Compatible Android client and guide"), destination: EncryptionGuidance.release)
                    case .engine:
                        Text(model.text("这一步做什么：选择软件启动时使用的 OpenCode V2。扫描已自动开始，等待扫描完成后选择，不需要填写命令或手动安装。\n\n1. 如果电脑还没有 OpenCode，或者你不确定选哪个，点击蓝色「选择内置环境」即可。完整安装包已经带好环境，无需另外下载 Node、Bun 或 OpenCode。\n2. 如果你已有 OpenCode V2，并希望继续使用它，核对版本和路径后点击对应的「选择此环境」。V1 不支持，不会出现在可用列表中。\n3. 出现「已选择」后，点击右下角「完成并进入 OpenCode」。软件会保存配置、启动 OpenCode 和 Bot，再直接进入网页端；失败会留在这里显示原因，不会把向导记为完成。没有结果时点击「重新扫描」；内置环境也不见时，请从完整安装包重新安装。\n\n首次进入网页端后，如提示没有可用模型，在 OpenCode 的模型 / 提供商连接界面（/connect）登录已有模型服务账号或配置自己的 API Key。这是模型提供商的凭据，不是 Bot Token；软件不能代你创建这些账号。\n\n向导成功完成只需一次。以后打开软件会自动启动 OpenCode、已配置的 Bot 和已登录 tg 的监听，首页就是 OpenCode 网页端，不再需要手动点启动。电脑必须保持运行和联网，手机才能通过 Bot 控制。", "Purpose: choose the OpenCode V2 environment the app will start. Scanning starts automatically. Wait for it to finish; no commands or manual installation are needed.\n\n1. If OpenCode is not installed, or you are unsure, click the blue Choose bundled environment button. The complete installer already includes Node, Bun and OpenCode; no separate downloads are needed.\n2. To keep using an existing OpenCode V2 installation, check its version and path and click Choose this environment. V1 is not supported and will not appear as an available choice.\n3. Once Selected appears, click Finish and open OpenCode at the bottom right. The app saves configuration, starts OpenCode and the Bot, and opens the web page. A failure keeps you here with an explanation and does not mark setup complete. If nothing appears, click Scan again; if even the bundled environment is missing, reinstall from the complete package.\n\nOn first entering the web page, if no model is available, use OpenCode's model / provider connection UI (/connect) to sign in to your existing model service or supply your own API key. These are provider credentials, not a Bot Token; the app cannot create those accounts for you.\n\nSuccessful setup is required only once. Later app launches automatically start OpenCode, the configured Bot and the signed-in tg listener, with the OpenCode web page as home. Your computer must remain running and online for remote Bot control."))
                        Button(model.text("重新扫描", "Scan again")) { model.scanEngines() }.disabled(model.busy)
                        ForEach(SetupRules.engineChoices(model.engines)) { engine in
                            VStack(alignment: .leading, spacing: 8) {
                                Text("OpenCode \(engine.version)").fontWeight(.medium)
                                Text(engine.path).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                                if engine.isBundled() {
                                    Button(model.setupEnginePath == engine.path ? model.text("已选择 · 内置环境", "Selected · Bundled") : model.text("选择内置环境（推荐）", "Choose bundled environment (Recommended)")) { model.setupEnginePath = engine.path }
                                        .buttonStyle(.borderedProminent).tint(.blue)
                                } else {
                                    Button(model.setupEnginePath == engine.path ? model.text("已选择", "Selected") : model.text("选择此环境", "Choose this environment")) { model.setupEnginePath = engine.path }
                                        .buttonStyle(.bordered)
                                }
                            }.padding(16).frame(maxWidth: .infinity, alignment: .leading).background(Color.white, in: RoundedRectangle(cornerRadius: 12))
                        }.disabled(model.busy)
                        if model.engines.isEmpty && !model.busy { Text(model.text("未找到有效环境，请重新扫描或重新安装完整离线包。", "No valid environment found. Rescan or reinstall the complete offline package.")) }
                    }
                }.frame(maxWidth: .infinity, alignment: .leading).textFieldStyle(.roundedBorder).padding(.vertical, 8)
            }
            if model.busy { HStack { ProgressView().controlSize(.small); Text(model.message) } }
            if !model.error.isEmpty { Text(model.error).foregroundStyle(.red).textSelection(.enabled) }
            HStack {
                if model.setupStep != .telegram { Button(model.text("上一步", "Back")) { model.previousSetupStep() }.disabled(model.busy) }
                Spacer()
                Button(model.setupStep == .engine ? model.text("完成并进入 OpenCode", "Finish and open OpenCode") : model.text("继续", "Continue")) { model.advanceSetup() }
                    .buttonStyle(.borderedProminent).disabled(model.busy || !model.setupCanContinue)
            }
        }.padding(32).frame(minWidth: 980, minHeight: 680).background(Color(red: 0.965, green: 0.97, blue: 0.985))
            .sheet(isPresented: Binding(get: { model.engineProgress != nil }, set: { _ in })) {
                if let progress = model.engineProgress {
                    OperationProgressDialog(title: model.engineProgressTitle, message: progress.phase.title(english: model.settings.locale == "en"), fraction: progress.fraction, detail: progress.detail)
                }
            }
    }
}
